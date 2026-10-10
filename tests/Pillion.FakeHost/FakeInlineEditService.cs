using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Pillion.Contracts;

namespace Pillion.FakeHost;

/// <summary>
/// A canned <see cref="IInlineEditService"/>: streams a deterministic replacement for the scope, line by line, naming the
/// local fake model in every chunk, and honours both cancellation paths (the stream's token and
/// <see cref="CancelEditAsync"/>).
/// </summary>
/// <remarks>
/// <para>The proposal is built from <see cref="InlineEditRequest.PreviousProposal"/> when refining, else the scope:</para>
/// <list type="bullet">
/// <item><description>No command: the line <c>// Edited by Pillion's fake host: &lt;instruction&gt;</c> (indented like the first line) above the code.</description></item>
/// <item><description><c>/doc</c>: a <c>/// &lt;summary&gt;Fake documentation.&lt;/summary&gt;</c> line above the code.</description></item>
/// <item><description><c>/fix</c>: one <c>// Fixed: CODE</c> line per attached diagnostic above the code.</description></item>
/// <item><description><c>/test</c>: a small test class, with an explanation saying where it belongs.</description></item>
/// <item><description><c>/explain</c>: no text; the final chunk's explanation describes the scope.</description></item>
/// </list>
/// <para>Instructions select special cases: <c>error</c> fails with <see cref="PillionErrorCode.ProviderUnavailable"/>
/// before any chunk; <c>late error</c> fails after the first line; <c>reset</c> streams a wrong first line, then
/// <see cref="InlineEditChunk.ResetText"/> and the real proposal; <c>slow</c> waits 1.5 s per line; <c>no change</c>
/// returns the code unchanged with a warning; <c>rejected</c> reports one rejected edit block. The delay between lines
/// is <c>PILLION_FAKEHOST_CHUNK_DELAY_MS</c> (120 ms by default). Only ids and counts are written to stderr.</para>
/// </remarks>
internal sealed class FakeInlineEditService : IInlineEditService
{
    private const string ModelId = "fake-local-coder";
    private const string ProviderId = "ollama";

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);
    private readonly TimeSpan _chunkDelay = TimeSpan.FromMilliseconds(
        int.TryParse(Environment.GetEnvironmentVariable("PILLION_FAKEHOST_CHUNK_DELAY_MS"), NumberStyles.None, CultureInfo.InvariantCulture, out var ms) ? ms : 120);

    public async IAsyncEnumerable<InlineEditChunk> EditAsync(InlineEditRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var instruction = request.Instruction ?? string.Empty;
        Console.Error.WriteLine(
            "fakehost: inline edit " + request.RequestId + " command " + request.Command + ", scope lines "
            + CountLines(request.ScopeText) + ", diagnostics " + request.Diagnostics.Count + ", profile " + request.Profile
            + (request.PreviousProposal is null ? string.Empty : ", refining") + (request.ConversationId is null ? string.Empty : ", conversation " + request.ConversationId));

        if (!Has(instruction, "late error") && Has(instruction, "error"))
        {
            throw Unavailable();
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _inFlight[request.RequestId] = cancellation;
        try
        {
            var delay = Has(instruction, "slow") ? TimeSpan.FromMilliseconds(1500) : _chunkDelay;
            var newline = request.ScopeText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var final = new InlineEditChunk { RequestId = request.RequestId, IsFinal = true, ModelId = ModelId, ProviderId = ProviderId };
            if (request.Command == InlineEditCommand.Explain)
            {
                await WaitAsync(request, delay, cancellation.Token).ConfigureAwait(false);
                final.Explanation = "This code has " + CountLines(request.ScopeText) + " line(s). (A canned explanation from Pillion's fake host.)";
                yield return final;
                Console.Error.WriteLine("fakehost: inline edit " + request.RequestId + " complete");
                yield break;
            }

            var proposal = Proposal(request, instruction, newline);
            var lines = SplitKeepingEnds(proposal);
            if (Has(instruction, "reset"))
            {
                await WaitAsync(request, delay, cancellation.Token).ConfigureAwait(false);
                yield return Chunk(request, "// a wrong early preview line" + newline);
                await WaitAsync(request, delay, cancellation.Token).ConfigureAwait(false);
                var reset = Chunk(request, lines[0]);
                reset.ResetText = true;
                yield return reset;
                lines.RemoveAt(0);
            }

            var sent = 0;
            foreach (var line in lines)
            {
                await WaitAsync(request, delay, cancellation.Token).ConfigureAwait(false);
                if (sent == 1 && Has(instruction, "late error"))
                {
                    throw Unavailable();
                }

                sent++;
                yield return Chunk(request, line);
            }

            if (request.Command == InlineEditCommand.Test)
            {
                final.Explanation = "Put this test class in the test project, in a file named after the class under test.";
            }

            if (Has(instruction, "no change"))
            {
                final.Warning = "The model proposed no change.";
            }

            if (Has(instruction, "rejected"))
            {
                final.RejectedBlocks = 1;
            }

            final.PromptTokens = (request.ScopeText.Length + request.TextBefore.Length + request.TextAfter.Length) / 4;
            final.CompletionTokens = proposal.Length / 4;
            yield return final;
            Console.Error.WriteLine("fakehost: inline edit " + request.RequestId + " complete after " + sent + " lines");
        }
        finally
        {
            _inFlight.TryRemove(request.RequestId, out _);
        }
    }

    public Task CancelEditAsync(string requestId, CancellationToken cancellationToken)
    {
        if (_inFlight.TryGetValue(requestId, out var cancellation))
        {
            Console.Error.WriteLine("fakehost: cancel inline edit " + requestId);
            cancellation.Cancel();
        }
        else
        {
            Console.Error.WriteLine("fakehost: cancel inline edit " + requestId + " (not in flight)");
        }

        return Task.CompletedTask;
    }

    private static string Proposal(InlineEditRequest request, string instruction, string newline)
    {
        var code = request.PreviousProposal ?? request.ScopeText;
        if (Has(instruction, "no change"))
        {
            return code;
        }

        var indent = new string(code.TakeWhile(c => c is ' ' or '\t').ToArray());
        return request.Command switch
        {
            InlineEditCommand.Doc => indent + "/// <summary>Fake documentation.</summary>" + newline + code,
            InlineEditCommand.Fix => string.Concat(request.Diagnostics.Select(d => indent + "// Fixed: " + d.Code + newline)) + code,
            InlineEditCommand.Test => "public class FakeTests" + newline + "{" + newline + "    [Fact]" + newline + "    public void Works() { }" + newline + "}",
            _ => indent + "// Edited by Pillion's fake host: " + instruction.Trim() + newline + code,
        };
    }

    private static async Task WaitAsync(InlineEditRequest request, TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("fakehost: inline edit " + request.RequestId + " cancelled");
            throw;
        }
    }

    private static InlineEditChunk Chunk(InlineEditRequest request, string text) => new()
    {
        RequestId = request.RequestId,
        Text = text,
        ModelId = ModelId,
        ProviderId = ProviderId,
    };

    private static PillionRpcException Unavailable() => new(new PillionError
    {
        Code = PillionErrorCode.ProviderUnavailable,
        Message = "The fake provider for Fake Local Coder is unreachable.",
        RetryHint = "Check the provider under Tools > Options > Pillion Programmer > Providers and keys, then retry.",
        Details = "inline-edit: fake failure",
    });

    private static bool Has(string instruction, string marker) => instruction.Contains(marker, StringComparison.OrdinalIgnoreCase);

    private static int CountLines(string text) => text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;

    private static List<string> SplitKeepingEnds(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text.Substring(start, i + 1 - start));
                start = i + 1;
            }
        }

        if (start < text.Length || lines.Count == 0)
        {
            lines.Add(text.Substring(start));
        }

        return lines;
    }
}
