using System;
using System.Threading.Tasks;

namespace Pillion.VisualStudio.IntegrationTests;

/// <summary>
/// Saved conversations in a real Visual Studio (work item 2.2): a conversation made through the host's conversation service is
/// still there, with its pins, after the host process is killed and restarted. It uses the shipped Pillion Host and its Local
/// Store in the user's data folder (no model, no fake host), and deletes the one conversation it made.
/// </summary>
public sealed partial class PillionPackageTests
{
    [IdeFact]
    [TestOrder(5)]
    public async Task ASavedConversation_SurvivesKillingTheHost()
    {
        await EnsurePackageLoadedAsync();
        await PillionHostChecks.WaitForReadyAsync(TimeSpan.FromSeconds(30), HangMitigatingCancellationToken);

        var report = await PillionHostChecks.ConversationSurvivesHostRestartAsync(TimeSpan.FromSeconds(15), HangMitigatingCancellationToken);

        Output.WriteLine($"Conversation {report.ConversationId}: host pid {report.Restart.KilledProcessId} killed, pid {report.Restart.NewProcessId} ready again after {report.Restart.ReadyAgainAfter.TotalMilliseconds:0} ms; title after: {report.TitleAfter ?? "(not found)"}; pins after: {string.Join(", ", report.PinNamesAfter)}.");
        Assert.NotEqual(report.Restart.KilledProcessId, report.Restart.NewProcessId);
        Assert.Equal("Integration test conversation", report.TitleAfter);
        Assert.Equal(["Test.cs"], report.PinNamesAfter);
        Assert.True(report.ListedAfter, "The new host did not list the conversation.");
    }
}
