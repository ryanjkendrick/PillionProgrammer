using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Pillion.Contracts;

namespace Pillion.FakeHost;

/// <summary>
/// A canned <see cref="IModelService"/>: lists one local and one cloud model and streams a fixed reply, in several
/// chunks, that names the model that answered (in the text and, as contract 1.1 hosts do, in every chunk's
/// <see cref="ChatChunk.ModelId"/> and <see cref="ChatChunk.ProviderId"/>). Honours both cancellation paths: the
/// stream's token (the client disposing the enumerator) and <see cref="CancelAsync"/> with the request id.
/// </summary>
/// <remarks>
/// <see cref="CompleteAsync"/> streams a canned C# completion the same way, so the extension's autocomplete pipeline can
/// be tested over the real pipe.
/// A last user message containing <c>/error</c> fails the request with a <see cref="PillionErrorCode.ProviderUnavailable"/>
/// error before any chunk, so the client's inline error path can be exercised; one containing <c>/partial</c> streams
/// two chunks and then ends with a final chunk carrying <see cref="ChatChunk.Error"/>. One containing <c>/markdown</c> is
/// answered with <see cref="MarkdownReply"/> (headings, a table, inline code, a link, an image, a C# block) and one
/// containing <c>/long</c> with <see cref="LongReply"/> (about 2,000 tokens of mixed Markdown), for the chat window's
/// rendering checks. A request without a model id
/// (Auto) is answered by the local model. The delay between chunks is <c>PILLION_FAKEHOST_CHUNK_DELAY_MS</c> (120 ms by
/// default). Only ids and counts are written to stderr, never text.
/// From contract 1.3: a last user message containing <c>/cite</c> gets a final chunk with two canned
/// <see cref="ChatChunk.Citations"/> (<see cref="CannedCitations"/>), and every request with a
/// <see cref="ChatRequest.ConversationId"/> is appended to the conversation service, as Pillion Host does.
/// </remarks>
internal sealed class FakeModelService(FakeRecorder? recorder = null, FakeConversationService? conversations = null) : IModelService
{
    public const string LocalModelId = "ollama/fake-local-coder";
    public const string CloudModelId = "fake-cloud/fake-cloud-chat";

    private static readonly ModelDescriptor[] Models =
    [
        new()
        {
            Id = LocalModelId,
            ModelId = "fake-local-coder",
            DisplayName = "Fake Local Coder",
            ProviderId = "ollama",
            IsLocal = true,
            Capabilities = ModelCapabilities.Chat | ModelCapabilities.Fim,
            IsAvailable = true,
            ContextWindow = 8192,
            Digest = "sha256:fake0local0digest",
        },
        new()
        {
            Id = CloudModelId,
            ModelId = "fake-cloud-chat",
            DisplayName = "Fake Cloud Chat",
            ProviderId = "fake-cloud",
            IsLocal = false,
            Capabilities = ModelCapabilities.Chat | ModelCapabilities.Tools,
            IsAvailable = true,
            ContextWindow = 128000,
        },
    ];

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);
    private readonly TimeSpan _chunkDelay = TimeSpan.FromMilliseconds(
        int.TryParse(Environment.GetEnvironmentVariable("PILLION_FAKEHOST_CHUNK_DELAY_MS"), NumberStyles.None, CultureInfo.InvariantCulture, out var ms) ? ms : 120);

    /// <summary>Finds the model a request names; Auto (no id) and unknown ids get the local model.</summary>
    /// <param name="modelId">The request's model id.</param>
    /// <returns>The model.</returns>
    public static ModelDescriptor Find(string? modelId) => Models.FirstOrDefault(m => m.Id == modelId) ?? Models[0];

    /// <summary>The citations a <c>/cite</c> reply ends with: the start of the current file (or a canned path), applying at offset 10, and a span for the whole reply.</summary>
    /// <param name="request">The chat request.</param>
    /// <returns>The citations.</returns>
    public static Citation[] CannedCitations(ChatRequest request)
    {
        var file = request.CurrentFile.Length > 0 ? request.CurrentFile : Path.Combine(FakeContextService.CannedRoot, "src", "Program.cs");
        return
        [
            new Citation { FilePath = file, StartLine = 0, EndLine = 4, ChunkId = FakeContextService.CannedIndexId + ":1", Breadcrumb = Path.GetFileName(file), ReplyOffset = 10 },
            new Citation { FilePath = Path.Combine(FakeContextService.CannedRoot, "src", "Runner.cs"), StartLine = 3, EndLine = 9, ChunkId = FakeContextService.CannedIndexId + ":12", Breadcrumb = "src/Runner.cs > Fake > Runner > Run", ReplyOffset = -1 },
        ];
    }

    public Task<ModelDescriptor[]> ListModelsAsync(CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("fakehost: list models");
        return Task.FromResult(Models);
    }

    public async IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = Find(request.ModelId);
        Console.Error.WriteLine(
            "fakehost: chat " + request.RequestId + " model " + model.Id + ", " + request.Messages.Count + " messages");
        recorder?.Chat(request, model);
        var started = System.Diagnostics.Stopwatch.StartNew();

        var lastUser = request.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
        if (lastUser.Contains("/error", StringComparison.OrdinalIgnoreCase))
        {
            throw new PillionRpcException(new PillionError
            {
                Code = PillionErrorCode.ProviderUnavailable,
                Message = "The fake provider for " + model.DisplayName + " is unreachable.",
                RetryHint = "Check the provider under Tools > Options > Pillion Programmer > Providers and keys, then retry.",
            });
        }

        var partial = lastUser.Contains("/partial", StringComparison.OrdinalIgnoreCase);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _inFlight[request.RequestId] = cancellation;
        try
        {
            var reply = lastUser.Contains("/markdown", StringComparison.OrdinalIgnoreCase) ? MarkdownReply(model)
                : lastUser.Contains("/long", StringComparison.OrdinalIgnoreCase) ? LongReply(model)
                : ReplyFor(model, request.Messages.Count);
            var pieces = Split(reply, 14).ToList();
            var sent = 0;
            foreach (var piece in partial ? pieces.Take(2) : pieces)
            {
                try
                {
                    await Task.Delay(_chunkDelay, cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("fakehost: chat " + request.RequestId + " cancelled");

                    // Like Pillion Host: what had been sent is saved as a stopped reply, unless nothing had.
                    if (sent > 0)
                    {
                        conversations?.Append(request, Chunk(request, model, string.Empty), string.Concat(pieces.Take(sent)), cancelled: true);
                    }

                    throw;
                }

                sent++;
                yield return Chunk(request, model, piece);
            }

            var final = Chunk(request, model, string.Empty);
            final.IsFinal = true;
            final.PromptTokens = 10 * request.Messages.Count;
            final.CompletionTokens = sent * 4;
            if (partial)
            {
                final.Error = new PillionError
                {
                    Code = PillionErrorCode.ProviderUnavailable,
                    Message = "The fake provider for " + model.DisplayName + " dropped the connection part way through the reply.",
                    RetryHint = "Retry the reply.",
                };
            }

            if (lastUser.Contains("/cite", StringComparison.OrdinalIgnoreCase))
            {
                final.Citations = CannedCitations(request);
            }

            recorder?.Finished(request, model, final.PromptTokens, final.CompletionTokens, started.ElapsedMilliseconds, partial ? "Failed" : "Succeeded");
            conversations?.CountOutbound();
            conversations?.Append(request, final, string.Concat(partial ? pieces.Take(2) : pieces));
            yield return final;
            Console.Error.WriteLine("fakehost: chat " + request.RequestId + (partial ? " failed part way" : " complete"));
        }
        finally
        {
            _inFlight.TryRemove(request.RequestId, out _);
        }
    }

    public Task CancelAsync(string requestId, CancellationToken cancellationToken)
    {
        if (_inFlight.TryGetValue(requestId, out var cancellation))
        {
            Console.Error.WriteLine("fakehost: cancel " + requestId);
            cancellation.Cancel();
        }
        else
        {
            Console.Error.WriteLine("fakehost: cancel " + requestId + " (not in flight)");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Streams a canned fill-in-the-middle completion in four-character chunks, one every chunk delay: a C# method body
    /// when <see cref="CompletionRequest.AllowMultiLine"/> is set (with a second member after it, so the client's
    /// block-end rule has something to cut), otherwise a single line followed by a line break and more text (so the
    /// single-line rule has something to cut). Honours the stream's token and <see cref="CancelAsync"/>.
    /// </summary>
    public async IAsyncEnumerable<CompletionChunk> CompleteAsync(CompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = Models[0];
        Console.Error.WriteLine(
            "fakehost: complete " + request.RequestId + (request.AllowMultiLine ? " multi-line" : " single-line")
            + ", prefix " + request.Prefix.Length + ", suffix " + request.Suffix.Length);

        var text = request.AllowMultiLine
            ? "\n    {\n        return numbers.Min();\n    }\n\n    public static int Other()\n    {\n        return 0;\n    }\n"
            : "Calculator.Sum(numbers));\n        Console.WriteLine(\"more\");";

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _inFlight[request.RequestId] = cancellation;
        try
        {
            var sent = 0;
            foreach (var piece in Split(text, 4))
            {
                try
                {
                    await Task.Delay(_chunkDelay, cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("fakehost: complete " + request.RequestId + " cancelled after " + sent + " chunks");
                    throw;
                }

                sent++;
                yield return new CompletionChunk { RequestId = request.RequestId, Text = piece, ModelId = model.ModelId, ProviderId = model.ProviderId };
            }

            yield return new CompletionChunk
            {
                RequestId = request.RequestId,
                IsFinal = true,
                ModelId = model.ModelId,
                ProviderId = model.ProviderId,
                PromptTokens = (request.Prefix.Length + request.Suffix.Length) / 4,
                CompletionTokens = sent,
            };
            Console.Error.WriteLine("fakehost: complete " + request.RequestId + " finished after " + sent + " chunks");
        }
        finally
        {
            _inFlight.TryRemove(request.RequestId, out _);
        }
    }

    private static ChatChunk Chunk(ChatRequest request, ModelDescriptor model, string text) => new()
    {
        RequestId = request.RequestId,
        Text = text,
        ModelId = model.ModelId,
        ProviderId = model.ProviderId,
    };

    private static string ReplyFor(ModelDescriptor model, int messageCount) =>
        "Hello from " + model.DisplayName + " (model " + model.ModelId + ", provider " + model.ProviderId + ", "
        + (model.IsLocal ? "local" : "cloud") + "). This is a canned reply from Pillion's fake host, streamed in "
        + "several chunks; the conversation so far has " + messageCount.ToString(CultureInfo.InvariantCulture)
        + " message(s).\n\n```csharp\n// A fenced code block, shown in a monospace block.\nConsole.WriteLine(\"Hello from "
        + model.ModelId + "\");\n```\n\nThat is the end of the reply from " + model.DisplayName + ".";

    /// <summary>A reply that exercises the chat window's Markdown rendering.</summary>
    internal static string MarkdownReply(ModelDescriptor model) =>
        "# Markdown check\n\n"
        + "Here is **bold**, *italic*, ~~struck through~~ and `inline code`, a [link to the Visual Studio docs]"
        + "(https://learn.microsoft.com/visualstudio/extensibility/) that asks before opening, and an image that is never "
        + "downloaded: ![Pillion logo](https://example.com/pillion-logo.png).\n\n"
        + "## A table\n\n"
        + "| Model | Where | First token |\n| :--- | :---: | ---: |\n"
        + "| Fake Local Coder | local | 120 ms |\n| Fake Cloud Chat | cloud | 450 ms |\n\n"
        + "## Steps\n\n"
        + "1. Open a C# file.\n2. Put the caret where the method should go.\n3. Press **Insert at cursor** on the block below.\n\n"
        + "```csharp\npublic static int Add(int a, int b)\n{\n    // Returns the sum of two numbers.\n    return a + b;\n}\n```\n\n"
        + "> A quote with a link that is never opened: [run this](javascript:alert(1)).\n\n"
        + "---\n\n"
        + "That is the end of the Markdown check from " + model.DisplayName + ".";

    /// <summary>About 2,000 tokens (8,000 characters) of mixed Markdown, for the incremental rendering measurement.</summary>
    internal static string LongReply(ModelDescriptor model)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("# A long answer from ").Append(model.DisplayName).Append("\n\n");
        for (var section = 1; builder.Length < 8000; section++)
        {
            var n = section.ToString(CultureInfo.InvariantCulture);
            builder.Append("## Part ").Append(n).Append("\n\n")
                .Append("This part explains step ").Append(n).Append(" in some detail, with **strong text**, *emphasis*, ")
                .Append("`InlineCode").Append(n).Append("()` and a [reference](https://example.com/docs/part-").Append(n)
                .Append("). The paragraph is long enough to wrap across several lines in a narrow tool window, which is ")
                .Append("what a real model's answer usually looks like.\n\n")
                .Append("- First point of part ").Append(n).Append("\n- Second point, with `code`\n- Third point\n\n")
                .Append("```csharp\npublic sealed class Part").Append(n).Append("\n{\n    public int Value { get; } = ")
                .Append(n).Append(";\n\n    public override string ToString() => \"Part ").Append(n).Append("\";\n}\n```\n\n");
            if (section % 3 == 0)
            {
                builder.Append("| Name | Value |\n| --- | ---: |\n| Part ").Append(n).Append(" | ").Append(n).Append(" |\n| Total | ")
                    .Append((section * 10).ToString(CultureInfo.InvariantCulture)).Append(" |\n\n");
            }
        }

        builder.Append("That is the end of the long answer.");
        return builder.ToString();
    }

    private static IEnumerable<string> Split(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
        {
            yield return text.Substring(i, Math.Min(size, text.Length - i));
        }
    }
}
