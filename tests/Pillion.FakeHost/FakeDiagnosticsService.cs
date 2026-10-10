using Pillion.Contracts;

namespace Pillion.FakeHost;

/// <summary>
/// A canned <see cref="IDiagnosticsService"/>: empty autocomplete counters, the Outbound records of the chat requests this
/// fake host served (<see cref="FakeRecorder"/>), and a Context Inspector preview built from the request without a model
/// (the system prompt, each pin, the current file and the message; a pin named with <c>huge</c> in it is reported as
/// dropped, and a cloud model's preview counts one redaction per pin).
/// </summary>
internal sealed class FakeDiagnosticsService(FakeRecorder recorder) : IDiagnosticsService
{
    public Task<AutocompleteHostCounters> GetAutocompleteCountersAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AutocompleteHostCounters { WindowSeconds = 300 });

    public Task<OutboundRecordDto[]> GetOutboundRecordsAsync(int maxCount, CancellationToken cancellationToken)
    {
        var count = maxCount > 0 ? maxCount : 200;
        Console.Error.WriteLine("fakehost: outbound records " + recorder.Records.Count);
        return Task.FromResult(recorder.Records.Reverse().Take(count).ToArray());
    }

    public Task<ContextReportDto> PreviewContextAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = FakeModelService.Find(request.ModelId);
        var items = new List<ContextReportItemDto> { new() { Kind = "system", Name = "System prompt", Tokens = 40, OriginalTokens = 40 } };
        for (var i = 0; i < request.Pins.Length; i++)
        {
            var pin = request.Pins[i];
            var huge = pin.DisplayName.Contains("huge", StringComparison.OrdinalIgnoreCase);
            items.Add(new ContextReportItemDto
            {
                Kind = "pin",
                Name = pin.DisplayName,
                Tokens = huge ? 0 : pin.TokenCount,
                OriginalTokens = pin.TokenCount,
                Dropped = huge,
                Note = huge ? "There was no room left in the budget." : string.Empty,
                PinIndex = i,
                RedactionCount = model.IsLocal ? 0 : 1,
            });
        }

        if (!string.IsNullOrEmpty(request.CurrentFile))
        {
            var tokens = (request.CurrentFileRegion ?? string.Empty).Length / 4;
            items.Add(new ContextReportItemDto { Kind = "currentFile", Name = Path.GetFileName(request.CurrentFile), Tokens = tokens, OriginalTokens = tokens });
        }

        var query = (request.Messages.Count > 0 ? request.Messages[^1].Content ?? string.Empty : string.Empty).Length / 4;
        items.Add(new ContextReportItemDto { Kind = "query", Name = "Your message", Tokens = query, OriginalTokens = query });
        Console.Error.WriteLine("fakehost: preview " + request.RequestId + " pins " + request.Pins.Length);
        return Task.FromResult(new ContextReportDto
        {
            ModelId = model.ModelId,
            ProviderId = model.ProviderId,
            IsLocal = model.IsLocal,
            ContextWindow = model.ContextWindow,
            OutputReserve = model.ContextWindow / 4,
            BudgetTokens = model.ContextWindow - (model.ContextWindow / 4) - 40,
            TotalTokens = items.Sum(i => i.Tokens),
            Items = [.. items],
            IsAssembled = request.Pins.Length > 0 || !string.IsNullOrEmpty(request.CurrentFile),
            Redacts = !model.IsLocal,
            RedactionCount = items.Sum(i => i.RedactionCount),
        });
    }
}
