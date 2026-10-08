using System.Threading;
using System.Threading.Tasks;

namespace Pillion.Contracts;

/// <summary>Settings storage and provider checks, implemented by the host and called by the VSIX.</summary>
public interface ISettingsService
{
    /// <summary>Gets a setting.</summary>
    /// <param name="key">The setting key.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The setting value as JSON, or <see langword="null"/> if it is not set.</returns>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Sets a setting.</summary>
    /// <param name="key">The setting key.</param>
    /// <param name="json">The setting value as JSON.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>A task that completes when the setting has been stored.</returns>
    Task SetAsync(string key, string json, CancellationToken cancellationToken);

    /// <summary>Tests a model provider's connection and credentials.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="cancellationToken">A token that cancels this call.</param>
    /// <returns>The test result.</returns>
    Task<ProviderTestResult> TestProviderAsync(string providerId, CancellationToken cancellationToken);
}
