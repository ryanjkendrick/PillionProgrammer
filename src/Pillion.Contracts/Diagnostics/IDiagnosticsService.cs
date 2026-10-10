using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>
/// Read-only diagnostics, implemented by the host and called by the VSIX: counts, durations, names and token figures;
/// never prompt or completion text.
/// </summary>
public interface IDiagnosticsService
{
    /// <summary>Gets the Model Gateway's autocomplete counters and the latencies of the last five minutes.</summary>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The counters.</returns>
    Task<AutocompleteHostCounters> GetAutocompleteCountersAsync(CancellationToken cancellationToken);

    /// <summary>Gets the most recent records of the Outbound log (every request sent to any model, from every task class).</summary>
    /// <param name="maxCount">The most records to return; 0 or less means 200. The host keeps a bounded number of records.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The records, newest first.</returns>
    Task<OutboundRecordDto[]> GetOutboundRecordsAsync(int maxCount, CancellationToken cancellationToken);

    /// <summary>
    /// Assembles the context of a chat request as sending it would, for the model it would be routed to, without calling
    /// any model (the Context Inspector). Older history that sending would summarise is reported as such.
    /// </summary>
    /// <param name="request">The request about to be sent.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The report.</returns>
    Task<ContextReportDto> PreviewContextAsync(ChatRequest request, CancellationToken cancellationToken);
}
