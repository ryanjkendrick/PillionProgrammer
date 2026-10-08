using System;

namespace Pillion.VisualStudio;

/// <summary>GUIDs used by the package. Must match the symbols in PillionPackage.vsct.</summary>
internal static class PackageGuids
{
    /// <summary>The package GUID as a string, for attributes.</summary>
    public const string PackageString = "992e3a80-c52f-4dcd-b4d9-5c78dc352352";

    /// <summary>The command set GUID as a string.</summary>
    public const string CommandSetString = "444aebc2-1c36-43a9-ab09-2c2d4e04a11d";

    /// <summary>The Output window pane GUID as a string.</summary>
    public const string OutputPaneString = "4b449750-19f6-4492-a222-d4cfe14749fb";

    /// <summary>The Pillion Chat tool window GUID as a string.</summary>
    public const string ChatToolWindowString = "3a84b873-8bad-4568-b3af-36f887cf71e5";

    /// <summary>The Providers and keys Options page GUID as a string.</summary>
    public const string ProvidersOptionPageString = "aa7d0fda-d0a0-44a3-b3ab-3695b123e30d";

    /// <summary>The Privacy Options page GUID as a string.</summary>
    public const string PrivacyOptionPageString = "9e9915ef-18df-4dc8-b32b-29a22f29439e";

    /// <summary>The command set GUID.</summary>
    public static readonly Guid CommandSet = new(CommandSetString);

    /// <summary>The Output window pane GUID.</summary>
    public static readonly Guid OutputPane = new(OutputPaneString);
}

/// <summary>Command ids used by the package. Must match the symbols in PillionPackage.vsct.</summary>
internal static class PackageIds
{
    /// <summary>The "Pillion: Show Status" command.</summary>
    public const int ShowStatusCommand = 0x0100;

    /// <summary>The "Pillion: Retry" command: resets the crash counter and relaunches Pillion Host.</summary>
    public const int RetryHostCommand = 0x0101;

    /// <summary>The "Pillion: Open Logs" command: opens the log folder in File Explorer.</summary>
    public const int OpenLogsCommand = 0x0102;

    /// <summary>The "Pillion: Open Chat" command on the Tools menu (default shortcut Ctrl+Alt+Shift+P, C).</summary>
    public const int OpenChatCommand = 0x0103;

    /// <summary>The "Pillion Chat" command under View &gt; Other Windows.</summary>
    public const int ViewChatCommand = 0x0104;
}
