using System;
using System.Collections.Generic;
using System.Globalization;

namespace Pillion.VisualStudio.Core.Autocomplete;

/// <summary>The other inline-suggestion providers Pillion shares the ghost-text slot with.</summary>
[Flags]
public enum CompetitorKind
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>GitHub Copilot completions (built into Visual Studio 18; an extension on 17.x).</summary>
    Copilot = 1,

    /// <summary>IntelliCode whole-line completions.</summary>
    IntelliCode = 2,

    /// <summary>A proposal from a source Pillion does not recognise.</summary>
    Other = 4,
}

/// <summary>What a competitor's own "completions on" setting says.</summary>
public enum CompetitorSetting
{
    /// <summary>Not readable through a public API, or never set (the product's default then applies).</summary>
    Unknown,

    /// <summary>On.</summary>
    On,

    /// <summary>Off.</summary>
    Off,
}

/// <summary>
/// What the start-up detection found (design section 5, Suggestion coexistence): which competing proposal sources are
/// exported to the editor (MEF metadata names, a public and version-independent signal), and what their settings say
/// where Visual Studio's settings manager returns a value. Whether Copilot is signed in has no public API, so Copilot,
/// and IntelliCode on Visual Studio 18, count as active only once one of their proposals has been displayed.
/// </summary>
public sealed class CompetitorDetection
{
    /// <summary>The MEF name of Copilot's proposal source provider (both versions).</summary>
    public const string CopilotSourceName = "CompletionsProposalSourceProvider";

    /// <summary>The MEF names of IntelliCode's proposal source providers (C# and C++).</summary>
    public static readonly IReadOnlyList<string> IntelliCodeSourceNames = ["IntelliCodeCSharpProposalSource", "IntelliCodeVCProposalSource", "IntelliCodeCPlusPlusProposalSource"];

    /// <summary>Initializes a new instance of the <see cref="CompetitorDetection"/> class.</summary>
    /// <param name="visualStudioMajorVersion">The major version of devenv.exe.</param>
    /// <param name="copilotPresent">Whether Copilot's proposal source is exported.</param>
    /// <param name="copilotCompletions">Copilot's completions setting.</param>
    /// <param name="intelliCodePresent">Whether an IntelliCode proposal source is exported.</param>
    /// <param name="intelliCodeCompletions">IntelliCode's whole-line completions setting.</param>
    public CompetitorDetection(int visualStudioMajorVersion, bool copilotPresent, CompetitorSetting copilotCompletions, bool intelliCodePresent, CompetitorSetting intelliCodeCompletions)
    {
        VisualStudioMajorVersion = visualStudioMajorVersion;
        CopilotPresent = copilotPresent;
        CopilotCompletions = copilotCompletions;
        IntelliCodePresent = intelliCodePresent;
        IntelliCodeCompletions = intelliCodeCompletions;
    }

    /// <summary>Gets the detection before it has run: nothing found.</summary>
    public static CompetitorDetection None { get; } = new(0, false, CompetitorSetting.Unknown, false, CompetitorSetting.Unknown);

    /// <summary>Gets the major version of devenv.exe.</summary>
    public int VisualStudioMajorVersion { get; }

    /// <summary>Gets a value indicating whether Copilot's proposal source is exported.</summary>
    public bool CopilotPresent { get; }

    /// <summary>Gets Copilot's completions setting.</summary>
    public CompetitorSetting CopilotCompletions { get; }

    /// <summary>Gets a value indicating whether an IntelliCode proposal source is exported.</summary>
    public bool IntelliCodePresent { get; }

    /// <summary>Gets IntelliCode's whole-line completions setting.</summary>
    public CompetitorSetting IntelliCodeCompletions { get; }

    /// <summary>Gets the competitors whose source is exported and whose setting is not off.</summary>
    public CompetitorKind Present =>
        (CopilotPresent && CopilotCompletions != CompetitorSetting.Off ? CompetitorKind.Copilot : CompetitorKind.None)
        | (IntelliCodePresent && IntelliCodeCompletions != CompetitorSetting.Off ? CompetitorKind.IntelliCode : CompetitorKind.None);

    /// <summary>
    /// Gets the competitors known to be active at start-up, without waiting for a proposal: IntelliCode on Visual Studio
    /// 2022, where its whole-line completions run locally (no sign-in) and draw Pillion's ghost text too, so they are on
    /// whenever Pillion's suggestions can be shown.
    /// </summary>
    public CompetitorKind ActiveAtStartup =>
        VisualStudioMajorVersion is > 0 and < 18 && IntelliCodePresent && IntelliCodeCompletions != CompetitorSetting.Off
            ? CompetitorKind.IntelliCode
            : CompetitorKind.None;

    /// <summary>Builds the detection from the names of the exported proposal source providers.</summary>
    /// <param name="visualStudioMajorVersion">The major version of devenv.exe.</param>
    /// <param name="sourceNames">The <c>Name</c> metadata of every <c>ProposalSourceProviderBase</c> export.</param>
    /// <param name="copilotCompletions">Copilot's completions setting.</param>
    /// <param name="intelliCodeCompletions">IntelliCode's whole-line completions setting.</param>
    /// <returns>The detection.</returns>
    public static CompetitorDetection FromSourceNames(int visualStudioMajorVersion, IEnumerable<string?> sourceNames, CompetitorSetting copilotCompletions, CompetitorSetting intelliCodeCompletions)
    {
        if (sourceNames is null)
        {
            throw new ArgumentNullException(nameof(sourceNames));
        }

        var copilot = false;
        var intelliCode = false;
        foreach (var name in sourceNames)
        {
            if (string.Equals(name, CopilotSourceName, StringComparison.Ordinal))
            {
                copilot = true;
            }
            else if (name is not null && IsIntelliCodeSource(name))
            {
                intelliCode = true;
            }
        }

        return new CompetitorDetection(visualStudioMajorVersion, copilot, copilotCompletions, intelliCode, intelliCodeCompletions);
    }

    /// <summary>Interprets a stored "completions on" value read from the settings manager.</summary>
    /// <param name="value">A boolean, an integer (0 default, 1 on, 2 off, as IntelliCode stores it) or a string; <see langword="null"/> when missing.</param>
    /// <returns>The setting.</returns>
    public static CompetitorSetting InterpretSetting(object? value) => value switch
    {
        null => CompetitorSetting.Unknown,
        bool b => b ? CompetitorSetting.On : CompetitorSetting.Off,
        int i => i == 2 ? CompetitorSetting.Off : i == 1 ? CompetitorSetting.On : CompetitorSetting.Unknown,
        long l => l == 2 ? CompetitorSetting.Off : l == 1 ? CompetitorSetting.On : CompetitorSetting.Unknown,
        string s when bool.TryParse(s, out var b) => b ? CompetitorSetting.On : CompetitorSetting.Off,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => InterpretSetting(i),
        _ => CompetitorSetting.Unknown,
    };

    /// <summary>Gets one line for the log.</summary>
    /// <returns>The line.</returns>
    public string Describe() => string.Format(
        CultureInfo.InvariantCulture,
        "Visual Studio {0}; Copilot source {1}, completions setting {2}; IntelliCode source {3}, completions setting {4}; active at start-up: {5}",
        VisualStudioMajorVersion,
        CopilotPresent ? "present" : "absent",
        CopilotCompletions,
        IntelliCodePresent ? "present" : "absent",
        IntelliCodeCompletions,
        ActiveAtStartup);

    private static bool IsIntelliCodeSource(string name)
    {
        foreach (var known in IntelliCodeSourceNames)
        {
            if (string.Equals(name, known, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Tells whose a displayed proposal is from its id.</summary>
public static class CompetitorProposals
{
    /// <summary>Classifies a proposal id that is not Pillion's.</summary>
    /// <param name="proposalId">The id.</param>
    /// <returns>
    /// <see cref="CompetitorKind.Copilot"/> for Copilot's completions and next edit suggestions (ids starting <c>cpt</c>
    /// or <c>nes</c>, as seen on 18.10), <see cref="CompetitorKind.IntelliCode"/> for IntelliCode's (a GUID, a colon and a
    /// number, as seen on 17.14), <see cref="CompetitorKind.None"/> for Pillion's own or an empty id, and
    /// <see cref="CompetitorKind.Other"/> otherwise.
    /// </returns>
    public static CompetitorKind Classify(string? proposalId)
    {
        if (string.IsNullOrEmpty(proposalId) || PillionProposalId.IsPillion(proposalId))
        {
            return CompetitorKind.None;
        }

        if (proposalId!.StartsWith("cpt", StringComparison.Ordinal) || proposalId.StartsWith("nes", StringComparison.Ordinal))
        {
            return CompetitorKind.Copilot;
        }

        var colon = proposalId.IndexOf(':');
        return colon == 36 && Guid.TryParse(proposalId.Substring(0, colon), out _) ? CompetitorKind.IntelliCode : CompetitorKind.Other;
    }

    /// <summary>Gets the product name shown to the user.</summary>
    /// <param name="kind">One or more competitors.</param>
    /// <returns>"Copilot", "IntelliCode", "Copilot and IntelliCode", or "another provider".</returns>
    public static string DisplayName(CompetitorKind kind)
    {
        var copilot = (kind & CompetitorKind.Copilot) != 0;
        var intelliCode = (kind & CompetitorKind.IntelliCode) != 0;
        return copilot && intelliCode ? "Copilot and IntelliCode"
            : copilot ? "Copilot"
            : intelliCode ? "IntelliCode"
            : "another provider";
    }
}
