namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>The outcome of the IntelliCode run-time check.</summary>
public enum IntelliCodeStatus
{
    /// <summary>Visual Studio 18 or later: the suggestion service is part of the platform, so nothing is needed.</summary>
    NotRequired,

    /// <summary>Visual Studio 2022 with IntelliCode installed and its automatic code completions on.</summary>
    Satisfied,

    /// <summary>Visual Studio 2022 without IntelliCode: ghost text cannot be shown.</summary>
    Missing,

    /// <summary>Visual Studio 2022 with IntelliCode's "Automatically generate code completions in the Editor" off.</summary>
    CompletionsOff,
}

/// <summary>
/// The IntelliCode requirement on Visual Studio 2022 (decision 2026-10-08, option (a)): on 17.x the Proposals API's
/// broker and suggestion service come from IntelliCode, and IntelliCode's "Automatically generate code completions in
/// the Editor" option switches them off for every provider. On 18.x the platform supplies them.
/// </summary>
public static class IntelliCodeRequirement
{
    /// <summary>The message shown in the status bar and the Output pane when the requirement is not met.</summary>
    public const string Message = "Inline suggestions need IntelliCode's automatic code completions on Visual Studio 2022";

    /// <summary>The IntelliCode package GUID (from its pkgdef).</summary>
    public const string PackageGuid = "237bde42-2754-4b15-adbd-0b6cece84300";

    /// <summary>
    /// The settings-manager key behind "Automatically generate code completions in the Editor" (Tools &gt; Options &gt;
    /// IntelliCode &gt; General), from IntelliCode's unified-settings registration: 0 is the default (on), 1 on, 2 off.
    /// </summary>
    public const string ShowCompletionsSetting = "Microsoft.VisualStudio.IntelliCode.ShowAICompletions";

    /// <summary>Evaluates the requirement.</summary>
    /// <param name="visualStudioMajorVersion">The major version of devenv.exe (17 for Visual Studio 2022).</param>
    /// <param name="intelliCodePresent">Whether IntelliCode (and its suggestion service) is installed.</param>
    /// <param name="completionsOn">Whether the automatic completions option is on; <see langword="null"/> when it could not be read (treated as on).</param>
    /// <returns>The status.</returns>
    public static IntelliCodeStatus Evaluate(int visualStudioMajorVersion, bool intelliCodePresent, bool? completionsOn)
    {
        if (visualStudioMajorVersion >= 18)
        {
            return IntelliCodeStatus.NotRequired;
        }

        if (!intelliCodePresent)
        {
            return IntelliCodeStatus.Missing;
        }

        return completionsOn == false ? IntelliCodeStatus.CompletionsOff : IntelliCodeStatus.Satisfied;
    }

    /// <summary>Reads the stored value of <see cref="ShowCompletionsSetting"/>.</summary>
    /// <param name="value">The stored value: an integer (0 default, 1 on, 2 off), a boolean or a string; <see langword="null"/> when unset.</param>
    /// <returns>Whether the option is on, or <see langword="null"/> when the value is not understood.</returns>
    public static bool? InterpretShowCompletions(object? value) => value switch
    {
        null => true, // Never set: the default is on.
        bool b => b,
        int i => i != 2,
        long l => l != 2,
        uint u => u != 2,
        string s when bool.TryParse(s, out var b) => b,
        string s when int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i) => i != 2,
        _ => null,
    };

    /// <summary>Gets the detail line for the Output pane.</summary>
    /// <param name="status">The status.</param>
    /// <returns>The line, or <see langword="null"/> when nothing needs saying.</returns>
    public static string? Describe(IntelliCodeStatus status) => status switch
    {
        IntelliCodeStatus.Missing => Message + ": IntelliCode is not installed. Add it with the Visual Studio Installer (any .NET workload includes it).",
        IntelliCodeStatus.CompletionsOff => Message + ": turn on Tools > Options > IntelliCode > General > \"Automatically generate code completions in the Editor\".",
        _ => null,
    };
}
