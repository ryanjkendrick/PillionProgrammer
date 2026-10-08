using Pillion.Common.Settings;

namespace Pillion.Common.Connectivity;

/// <summary>Supplies the effective settings to the connectivity monitor and says when they may have changed.</summary>
public interface IConnectivitySettingsProvider
{
    /// <summary>Raised when the user settings or the repository policies changed. May be raised on any thread.</summary>
    event EventHandler? Changed;

    /// <summary>Gets the current effective settings: user settings with repository policies applied.</summary>
    /// <returns>The effective settings.</returns>
    EffectiveSettings GetEffectiveSettings();
}

/// <summary>Supplies the policies of the repositories that are currently in scope (for example the open solution).</summary>
public interface IRepositoryPolicyProvider
{
    /// <summary>Raised when the set of policies changed.</summary>
    event EventHandler? Changed;

    /// <summary>Gets the policies in scope.</summary>
    /// <returns>The policies, or an empty list.</returns>
    IReadOnlyList<RepositoryPolicy> GetPolicies();
}

/// <summary>
/// An <see cref="IRepositoryPolicyProvider"/> that holds the policies it was last given. The component that knows which
/// repositories are open calls <see cref="SetPolicies"/> whenever that set changes. Thread-safe.
/// </summary>
public sealed class RepositoryPolicyRegistry : IRepositoryPolicyProvider
{
    private readonly object _gate = new();
    private IReadOnlyList<RepositoryPolicy> _policies = [];

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public IReadOnlyList<RepositoryPolicy> GetPolicies()
    {
        lock (_gate)
        {
            return _policies;
        }
    }

    /// <summary>Replaces the policies in scope and raises <see cref="Changed"/>.</summary>
    /// <param name="policies">The policies of the repositories now in scope.</param>
    public void SetPolicies(IEnumerable<RepositoryPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        lock (_gate)
        {
            _policies = [.. policies];
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// The <see cref="IConnectivitySettingsProvider"/> that combines the <see cref="SettingsStore"/> with the repository
/// policies through <see cref="EffectiveSettings.Resolve"/>.
/// </summary>
public sealed class StoreConnectivitySettingsProvider : IConnectivitySettingsProvider, IDisposable
{
    private readonly SettingsStore _store;
    private readonly IRepositoryPolicyProvider? _policies;

    /// <summary>Initializes a new instance of the <see cref="StoreConnectivitySettingsProvider"/> class.</summary>
    /// <param name="store">The settings store.</param>
    /// <param name="policies">The repository policies in scope, or <see langword="null"/> for none.</param>
    public StoreConnectivitySettingsProvider(SettingsStore store, IRepositoryPolicyProvider? policies = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _policies = policies;
        _store.Changed += OnChanged;
        if (_policies is not null)
        {
            _policies.Changed += OnPoliciesChanged;
        }
    }

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public EffectiveSettings GetEffectiveSettings() =>
        EffectiveSettings.Resolve(_store.GetSettings(), _policies?.GetPolicies());

    /// <inheritdoc/>
    public void Dispose()
    {
        _store.Changed -= OnChanged;
        if (_policies is not null)
        {
            _policies.Changed -= OnPoliciesChanged;
        }
    }

    private void OnChanged(object? sender, SettingsChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnPoliciesChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
