using System;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// An <see cref="IdeFactAttribute"/> for the Gate 2 scenarios (<c>PillionPackageTests.Gate2.cs</c>, work item 2.17), which
/// need Ollama, the bundled embedding model, the generated solution and the prebuilt 100,000-chunk index that
/// <c>tools/gate2/Invoke-Gate2.ps1</c> prepares. Skipped unless both <see cref="GateScenarioFactAttribute.EnabledVariable"/>
/// and <see cref="EnabledVariable"/> are <c>1</c> when the tests are discovered, so CI and the Gate 1 script report them as
/// skipped.
/// </summary>
public sealed class Gate2ScenarioFactAttribute : IdeFactAttribute
{
    /// <summary>The variable that turns the Gate 2 scenarios on (with <c>PILLION_GATE_SCENARIOS=1</c>).</summary>
    public const string EnabledVariable = "PILLION_GATE2_SCENARIOS";

    /// <summary>Initializes a new instance of the <see cref="Gate2ScenarioFactAttribute"/> class.</summary>
    public Gate2ScenarioFactAttribute()
    {
        if (!IsEnabled)
        {
            Skip = $"Gate 2 scenario: needs Ollama, the generated solution and the prebuilt index; run by tools/gate2/Invoke-Gate2.ps1 ({GateScenarioFactAttribute.EnabledVariable}=1 and {EnabledVariable}=1).";
        }
    }

    /// <summary>Gets a value indicating whether the Gate 2 scenarios are turned on in this process.</summary>
    public static bool IsEnabled =>
        GateScenarioFactAttribute.IsEnabled && string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal);
}

/// <summary>
/// The check that runs in a second Visual Studio session of a Gate 2 run (after a restart): only when
/// <c>PILLION_GATE2_RESTART_CHECK=1</c> as well.
/// </summary>
public sealed class Gate2RestartFactAttribute : IdeFactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="Gate2RestartFactAttribute"/> class.</summary>
    public Gate2RestartFactAttribute()
    {
        if (!Gate2ScenarioFactAttribute.IsEnabled || !string.Equals(Environment.GetEnvironmentVariable("PILLION_GATE2_RESTART_CHECK"), "1", StringComparison.Ordinal))
        {
            Skip = "Gate 2 restart check: run by tools/gate2/Invoke-Gate2.ps1 in a second Visual Studio session (PILLION_GATE2_RESTART_CHECK=1).";
        }
    }
}
