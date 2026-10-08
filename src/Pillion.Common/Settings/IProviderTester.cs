using Pillion.Contracts;

namespace Pillion.Common.Settings;

/// <summary>Checks that a configured model provider can be reached with its credentials. The Model Gateway implements this.</summary>
public interface IProviderTester
{
    /// <summary>Tests a provider.</summary>
    /// <param name="providerId">The provider id from the <c>providers</c> setting.</param>
    /// <param name="cancellationToken">A token that cancels the test.</param>
    /// <returns>The result of the test.</returns>
    Task<ProviderTestResult> TestAsync(string providerId, CancellationToken cancellationToken);
}

/// <summary>The default <see cref="IProviderTester"/>, used until a real one is registered; it always reports failure.</summary>
public sealed class NoProviderTester : IProviderTester
{
    /// <summary>The message returned for every provider.</summary>
    public const string Message = "No provider tester registered";

    /// <inheritdoc/>
    public Task<ProviderTestResult> TestAsync(string providerId, CancellationToken cancellationToken)
        => Task.FromResult(new ProviderTestResult
        {
            ProviderId = providerId,
            IsSuccess = false,
            Message = Message,
        });
}
