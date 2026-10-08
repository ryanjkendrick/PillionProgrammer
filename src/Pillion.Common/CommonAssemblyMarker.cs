namespace Pillion.Common;

// Pillion.Common holds host-side services shared by the rest of Pillion Host:
//   - settings storage and schema (Pillion.Common.Settings, backing ISettingsService),
//   - secret storage for provider API keys (Pillion.Common.Secrets, Windows Credential Manager),
//   - redaction of secrets and personal data from logs and prompts,
//   - connectivity detection for offline-first routing.
// Settings and secrets are implemented; redaction and connectivity detection are still to come.

/// <summary>Placeholder type that identifies the Pillion.Common assembly until real services are added.</summary>
public static class CommonAssemblyMarker
{
    /// <summary>Gets the name of this assembly.</summary>
    public static string AssemblyName => typeof(CommonAssemblyMarker).Assembly.GetName().Name ?? string.Empty;
}
