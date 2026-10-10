using System;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// An <see cref="IdeFactAttribute"/> for the Gate 1 performance scenarios (<c>PillionPackageTests.Gate1.cs</c>), which need
/// a local model in Ollama and the settings <c>tools/gate1/Invoke-Gate1.ps1</c> writes. Skipped unless the environment
/// variable <see cref="EnabledVariable"/> is <c>1</c> when the tests are discovered, so CI (no model) and a plain
/// <c>Invoke-IdeTests.ps1</c> run report them as skipped.
/// </summary>
public sealed class GateScenarioFactAttribute : IdeFactAttribute
{
    /// <summary>The variable that turns the scenarios on.</summary>
    public const string EnabledVariable = "PILLION_GATE_SCENARIOS";

    /// <summary>Initializes a new instance of the <see cref="GateScenarioFactAttribute"/> class.</summary>
    public GateScenarioFactAttribute()
    {
        if (!IsEnabled)
        {
            Skip = $"Gate 1 scenario: needs Ollama and the gate settings; run by tools/gate1/Invoke-Gate1.ps1 ({EnabledVariable}=1).";
        }
    }

    /// <summary>Gets a value indicating whether the scenarios are turned on in this process.</summary>
    public static bool IsEnabled => string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal);
}
