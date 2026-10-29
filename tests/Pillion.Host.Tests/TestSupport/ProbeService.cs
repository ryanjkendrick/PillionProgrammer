using System.Runtime.CompilerServices;
using Pillion.Contracts;

namespace Pillion.Host.Tests.TestSupport;

/// <summary>A test-only RPC contract for streaming and error mapping. Public so StreamJsonRpc can proxy it.</summary>
public interface IProbeService
{
    IAsyncEnumerable<ChatChunk> StreamAsync(int count, CancellationToken cancellationToken);

    Task<int> EchoAsync(int value, CancellationToken cancellationToken);

    Task ThrowPillionAsync(CancellationToken cancellationToken);

    Task ThrowUnexpectedAsync(CancellationToken cancellationToken);

    Task ThrowCancelledAsync(CancellationToken cancellationToken);

    Task WaitUntilCancelledAsync(CancellationToken cancellationToken);
}

internal sealed class ProbeService : IProbeService
{
    public const string SecretInUnexpectedException = "do-not-leak-this-detail";

    public static readonly PillionError ProviderError = new()
    {
        Code = PillionErrorCode.ProviderUnavailable,
        Message = "The provider is down.",
        RetryHint = "Retry in 30 s.",
        Details = "status 503",
    };

    public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> WaitEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async IAsyncEnumerable<ChatChunk> StreamAsync(int count, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < count; i++)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatChunk { RequestId = "probe", Text = $"chunk {i}", IsFinal = i == count - 1 };
        }
    }

    public Task<int> EchoAsync(int value, CancellationToken cancellationToken) => Task.FromResult(value);

    public Task ThrowPillionAsync(CancellationToken cancellationToken) => throw new PillionRpcException(ProviderError);

    public Task ThrowUnexpectedAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(SecretInUnexpectedException);

    public Task ThrowCancelledAsync(CancellationToken cancellationToken) => throw new OperationCanceledException();

    public async Task WaitUntilCancelledAsync(CancellationToken cancellationToken)
    {
        WaitStarted.TrySetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally
        {
            WaitEnded.TrySetResult(cancellationToken.IsCancellationRequested);
        }
    }
}

/// <summary>A VSIX-side editor service for host-to-VSIX call tests.</summary>
internal sealed class FakeEditorService : IEditorService
{
    public Task<ApplyResult> ApplyChangesetAsync(Changeset changeset, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<PreviewDecision> ShowPreviewAsync(Changeset changeset, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<OpenDocument[]> GetOpenDocumentsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new[] { new OpenDocument { Path = @"C:\src\Program.cs" } });

    public Task<DocumentText> ReadDocumentAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(new DocumentText { Path = path, IsOpen = false });
}
