using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Pillion.Contracts;
using Pillion.Host.InlineEdit;
using Pillion.ModelGateway;
using Pillion.ModelGateway.Editing;

namespace Pillion.Host.Tests.InlineEdit;

/// <summary>
/// An <see cref="IModelGateway"/> that answers chat calls with scripted replies, streamed in half-line pieces, and
/// records every request. A reply can hold back the rest of its stream until a gate is opened.
/// </summary>
internal sealed class ScriptedGateway : IModelGateway
{
    private readonly ConcurrentQueue<Script> _scripts = new();

    public ConcurrentQueue<(ChatRequest Request, TaskClass TaskClass)> Calls { get; } = new();

    public int PiecesSent { get; private set; }

    public ScriptedGateway Reply(string text, TaskCompletionSource? gate = null, int gateAfterPieces = 0)
    {
        _scripts.Enqueue(new Script(text, null, gate, gateAfterPieces, null));
        return this;
    }

    public ScriptedGateway Fail(PillionError error)
    {
        _scripts.Enqueue(new Script(string.Empty, error, null, 0, null));
        return this;
    }

    public ScriptedGateway FailAfterText(string text, PillionError error)
    {
        _scripts.Enqueue(new Script(text, null, null, 0, error));
        return this;
    }

    public async IAsyncEnumerable<ChatChunk> ChatAsync(ChatRequest request, TaskClass taskClass, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Calls.Enqueue((request, taskClass));
        if (!_scripts.TryDequeue(out var script))
        {
            throw new InvalidOperationException("The test did not script another reply.");
        }

        if (script.Error is not null)
        {
            throw new PillionRpcException(script.Error);
        }

        var pieces = Pieces(script.Text);
        for (var i = 0; i < pieces.Count; i++)
        {
            if (script.Gate is not null && i == script.GateAfterPieces)
            {
                await script.Gate.Task.WaitAsync(cancellationToken);
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            PiecesSent++;
            yield return new ChatChunk { RequestId = request.RequestId, Text = pieces[i], ModelId = "fake-coder", ProviderId = "fake" };
        }

        yield return new ChatChunk
        {
            RequestId = request.RequestId,
            IsFinal = true,
            ModelId = "fake-coder",
            ProviderId = "fake",
            PromptTokens = 100,
            CompletionTokens = 10,
            Error = script.StreamError,
        };
    }

    public IAsyncEnumerable<CompletionChunk> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ProviderTestResult> TestProviderAsync(string providerId, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>Splits text into pieces of about half a line, each line's newline ending a piece.</summary>
    public static List<string> Pieces(string text)
    {
        var pieces = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var half = line.Length / 2;
            if (half > 0)
            {
                pieces.Add(line[..half]);
            }

            pieces.Add(line[half..] + "\n");
        }

        // The last split element had no newline after it.
        pieces[^1] = pieces[^1][..^1];
        if (pieces[^1].Length == 0)
        {
            pieces.RemoveAt(pieces.Count - 1);
        }

        return pieces;
    }

    private sealed record Script(string Text, PillionError? Error, TaskCompletionSource? Gate, int GateAfterPieces, PillionError? StreamError);
}

internal static class InlineEditTestSupport
{
    public const string Before = "using System;\n\nnamespace Demo;\n\npublic class Orders\n{\n";

    public const string Scope =
        "    public void Place(Order order)\n" +
        "    {\n" +
        "        if (order is null)\n" +
        "        {\n" +
        "            throw new ArgumentNullException(nameof(order));\n" +
        "        }\n" +
        "\n" +
        "        _store.Add(order);\n" +
        "    }\n";

    public const string After = "}\n";

    /// <summary>A reply that extracts the validation into a method.</summary>
    public const string ExtractReply =
        "<<<<<<< SEARCH\n" +
        "        if (order is null)\n" +
        "        {\n" +
        "            throw new ArgumentNullException(nameof(order));\n" +
        "        }\n" +
        "\n" +
        "        _store.Add(order);\n" +
        "    }\n" +
        "=======\n" +
        "        Validate(order);\n" +
        "        _store.Add(order);\n" +
        "    }\n" +
        "\n" +
        "    private static void Validate(Order order)\n" +
        "    {\n" +
        "        if (order is null)\n" +
        "        {\n" +
        "            throw new ArgumentNullException(nameof(order));\n" +
        "        }\n" +
        "    }\n" +
        ">>>>>>> REPLACE\n" +
        "Moved the null check into Validate.";

    public const string Extracted =
        "    public void Place(Order order)\n" +
        "    {\n" +
        "        Validate(order);\n" +
        "        _store.Add(order);\n" +
        "    }\n" +
        "\n" +
        "    private static void Validate(Order order)\n" +
        "    {\n" +
        "        if (order is null)\n" +
        "        {\n" +
        "            throw new ArgumentNullException(nameof(order));\n" +
        "        }\n" +
        "    }\n";

    public const string BadBlockReply =
        "<<<<<<< SEARCH\n" +
        "        if (order == null)\n" +
        "=======\n" +
        "        Validate(order);\n" +
        ">>>>>>> REPLACE\n";

    public const string MissingSemicolonReply =
        "<<<<<<< SEARCH\n" +
        "        _store.Add(order);\n" +
        "=======\n" +
        "        _store.Add(order)\n" +
        ">>>>>>> REPLACE\n";

    public static InlineEditRequest Request(string instruction = "extract the validation into a method", InlineEditCommand command = InlineEditCommand.None) => new()
    {
        RequestId = "ie-" + Guid.NewGuid().ToString("N")[..8],
        FilePath = @"C:\src\Demo\Orders.cs",
        LanguageId = "csharp",
        ScopeText = Scope,
        ScopeStartLine = 6,
        ScopeStartColumn = 0,
        TextBefore = Before,
        TextAfter = After,
        Instruction = instruction,
        Command = command,
    };

    /// <summary>An engine in the block format (whole-scope mode off): the replies of these tests are edit blocks.</summary>
    public static InlineEditEngine Engine(IModelGateway gateway, InlineEditConversations? conversations = null, int wholeScopeMaxLines = 0) =>
        new(
            gateway,
            new InlineEditPromptBuilder(),
            RoslynSyntaxValidator.Instance,
            conversations ?? new InlineEditConversations(),
            options: new InlineEditOptions { WholeScopeMaxLines = wholeScopeMaxLines });

    /// <summary>An engine with whole-scope mode on for scopes of up to <paramref name="wholeScopeMaxLines"/> lines.</summary>
    public static InlineEditEngine WholeEngine(IModelGateway gateway, InlineEditConversations? conversations = null, int wholeScopeMaxLines = InlineEditOptions.DefaultWholeScopeMaxLines) =>
        Engine(gateway, conversations, wholeScopeMaxLines);

    public static async Task<List<InlineEditChunk>> CollectAsync(IAsyncEnumerable<InlineEditChunk> stream, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        var list = new List<InlineEditChunk>();
        await foreach (var chunk in stream.WithCancellation(cts.Token))
        {
            list.Add(chunk);
        }

        return list;
    }

    /// <summary>Assembles the proposal the way the VSIX does: append each chunk's text, starting again at each reset.</summary>
    public static string Assemble(IEnumerable<InlineEditChunk> chunks)
    {
        var text = new System.Text.StringBuilder();
        foreach (var chunk in chunks)
        {
            if (chunk.ResetText)
            {
                text.Clear();
            }

            text.Append(chunk.Text);
        }

        return text.ToString();
    }
}
