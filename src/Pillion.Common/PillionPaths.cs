using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pillion.Common;

/// <summary>
/// The on-disk layout of Pillion's per-user data. The root defaults to <c>%LOCALAPPDATA%\PillionProgrammer</c>
/// and can be overridden (tests do this) by passing a root to the constructor.
/// </summary>
public sealed partial class PillionPaths
{
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="PillionPaths"/> class.</summary>
    /// <param name="root">
    /// The data root, or <see langword="null"/> to use <c>%LOCALAPPDATA%\PillionProgrammer</c>.
    /// </param>
    /// <param name="logger">An optional logger used to report problems that do not stop start-up.</param>
    /// <exception cref="InvalidOperationException">No root was given and the local application data folder is unavailable.</exception>
    public PillionPaths(string? root = null, ILogger<PillionPaths>? logger = null)
    {
        _logger = logger ?? NullLogger<PillionPaths>.Instance;

        if (string.IsNullOrWhiteSpace(root))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
            {
                throw new InvalidOperationException("The local application data folder could not be determined, so the Pillion data root cannot be chosen.");
            }

            root = Path.Combine(localAppData, "PillionProgrammer");
        }

        Root = Path.GetFullPath(root);
    }

    /// <summary>Gets the data root directory.</summary>
    public string Root { get; }

    /// <summary>Gets the path of <c>settings.json</c>.</summary>
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Gets the path of <c>rules.md</c> (user-wide rules for the assistant).</summary>
    public string RulesFile => Path.Combine(Root, "rules.md");

    /// <summary>Gets the <c>prompts</c> directory (user prompt templates).</summary>
    public string PromptsDirectory => Path.Combine(Root, "prompts");

    /// <summary>Gets the path of <c>hooks.json</c>.</summary>
    public string HooksFile => Path.Combine(Root, "hooks.json");

    /// <summary>Gets the path of <c>trust.json</c> (which repositories the user has trusted).</summary>
    public string TrustFile => Path.Combine(Root, "trust.json");

    /// <summary>Gets the <c>logs</c> directory.</summary>
    public string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>Gets the <c>crash</c> directory.</summary>
    public string CrashDirectory => Path.Combine(Root, "crash");

    /// <summary>Gets the <c>models</c> directory (downloaded local models).</summary>
    public string ModelsDirectory => Path.Combine(Root, "models");

    /// <summary>Gets the <c>index</c> directory (code index data).</summary>
    public string IndexDirectory => Path.Combine(Root, "index");

    /// <summary>Gets the path of <c>conversations.db</c>.</summary>
    public string ConversationsDatabase => Path.Combine(Root, "conversations.db");

    /// <summary>Gets the <c>audit</c> directory.</summary>
    public string AuditDirectory => Path.Combine(Root, "audit");

    /// <summary>Gets the <c>checkpoints</c> directory (agent checkpoints).</summary>
    public string CheckpointsDirectory => Path.Combine(Root, "checkpoints");

    /// <summary>Gets the <c>browser-profiles</c> directory (agent browser profiles).</summary>
    public string BrowserProfilesDirectory => Path.Combine(Root, "browser-profiles");

    /// <summary>
    /// Creates the data root if it does not exist. When this call creates it, the directory is restricted to the
    /// current user and SYSTEM (full control, inheritance from the parent removed). A failure to restrict access
    /// is logged and does not stop start-up.
    /// </summary>
    /// <returns><see langword="true"/> if the root was created by this call; <see langword="false"/> if it already existed.</returns>
    public bool EnsureCreated()
    {
        if (Directory.Exists(Root))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(Root);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                CreateRestrictedDirectory(Root);
                return true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PlatformNotSupportedException or System.Security.SecurityException)
            {
                LogAclFailed(_logger, ex, Root);
            }
        }

        Directory.CreateDirectory(Root);
        return true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CreateRestrictedDirectory(string path)
    {
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current user's SID is unavailable.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

        new DirectoryInfo(path).Create(security);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not restrict access to the Pillion data directory '{Path}' to the current user; it was created with default permissions.")]
    private static partial void LogAclFailed(ILogger logger, Exception exception, string path);
}
