using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>Read-only diagnostics, implemented by the host and called by the VSIX. Counts and durations only; never text.</summary>
public interface IDiagnosticsService
{
    /// <summary>Gets the Model Gateway's autocomplete counters and the latencies of the last five minutes.</summary>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The counters.</returns>
    Task<AutocompleteHostCounters> GetAutocompleteCountersAsync(CancellationToken cancellationToken);
}
