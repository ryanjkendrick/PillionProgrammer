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
}
