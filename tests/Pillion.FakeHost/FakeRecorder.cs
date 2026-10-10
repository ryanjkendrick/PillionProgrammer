using System.Collections.Concurrent;
using System.Text.Json;
using Pillion.Contracts;

namespace Pillion.FakeHost;

/// <summary>
/// What the fake host was asked: every chat request becomes an Outbound record (served by
/// <see cref="FakeDiagnosticsService.GetOutboundRecordsAsync"/>) and, when <c>PILLION_FAKEHOST_RECORD</c> names a file, one
/// JSON line in it with the request id, the model, the message count and the pins (kind and name) and whether a current file
/// came with it. The Visual Studio integration tests read that file. Never the message text or the pin content.
/// </summary>
internal sealed class FakeRecorder
{
    private readonly ConcurrentQueue<OutboundRecordDto> _records = new();
    private readonly string? _file = Environment.GetEnvironmentVariable("PILLION_FAKEHOST_RECORD");
    private readonly object _gate = new();

    /// <summary>Gets the Outbound records, oldest first.</summary>
    public IReadOnlyList<OutboundRecordDto> Records => [.. _records];

    /// <summary>Records a chat request as it arrives.</summary>
    /// <param name="request">The request.</param>
    /// <param name="model">The model that answers it.</param>
    public void Chat(ChatRequest request, ModelDescriptor model)
    {
        Console.Error.WriteLine(
            "fakehost: chat " + request.RequestId + " pins " + request.Pins.Length + ", current file " + (request.CurrentFile.Length > 0 ? "yes" : "no"));
        if (string.IsNullOrEmpty(_file))
        {
            return;
        }

        var line = JsonSerializer.Serialize(new
        {
            requestId = request.RequestId,
            model = model.Id,
            messages = request.Messages.Count,
            conversationId = request.ConversationId,
            pins = request.Pins.Select(p => new { kind = p.Kind, name = p.DisplayName, sensitive = p.IsSensitive }).ToArray(),
            currentFile = Path.GetFileName(request.CurrentFile),
        });
        lock (_gate)
        {
            File.AppendAllText(_file!, line + Environment.NewLine);
        }
    }

    /// <summary>Adds the Outbound record of a finished chat request.</summary>
    /// <param name="request">The request.</param>
    /// <param name="model">The model that answered.</param>
    /// <param name="promptTokens">The prompt tokens reported.</param>
    /// <param name="completionTokens">The completion tokens reported.</param>
    /// <param name="durationMs">How long it took.</param>
    /// <param name="outcome">How it ended.</param>
    public void Finished(ChatRequest request, ModelDescriptor model, int promptTokens, int completionTokens, long durationMs, string outcome) =>
        _records.Enqueue(new OutboundRecordDto
        {
            TimestampUtc = DateTime.UtcNow,
            RequestId = request.RequestId,
            ProviderId = model.ProviderId,
            ModelId = model.ModelId,
            TaskClass = TaskClass.Chat,
            IsLocal = model.IsLocal,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            RedactionCount = model.IsLocal ? 0 : request.Pins.Length,
            ContextItems = request.Pins.Length == 0 && request.CurrentFile.Length == 0 ? 0 : 2 + request.Pins.Length + (request.CurrentFile.Length > 0 ? 1 : 0),
            DurationMs = durationMs,
            Outcome = outcome,
        });
}
