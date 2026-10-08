using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace ProposalsSpike
{
    /// <summary>
    /// Optional ProposalManager: tells the inline-completions engine "yes, generate a proposal here" on lines that
    /// contain (or follow) one of the spike's markers and defers (returns false) everywhere else.
    /// Create %TEMP%\ProposalsSpike.nomanager to make it always defer, to test whether a source is called without it.
    /// Note: TryGetIsProposalPosition takes <c>ref bool</c> (not <c>out</c>) in the 17.14 SDK.
    /// </summary>
    [Export(typeof(ProposalManagerProviderBase))]
    [Name("PillionProgrammer.ProposalsSpikeManager")]
    [Order(Before = "CompletionsProposalManagerProvider")]
    [Order(Before = ProposalManagerProviders.IntelliCodeCSharpProposalManager)]
    [Order(Before = "Highest Priority")]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class SpikeProposalManagerProvider : ProposalManagerProviderBase
    {
        public override Task<ProposalManagerBase> GetProposalManagerAsync(ITextView view, CancellationToken cancel)
        {
            SpikeLog.Write("PROVIDER", $"GetProposalManagerAsync contentType={view.TextBuffer.ContentType.TypeName}");
            return Task.FromResult<ProposalManagerBase>(new Manager());
        }

        private sealed class Manager : ProposalManagerBase
        {
            public override bool TryGetIsProposalPosition(VirtualSnapshotPoint caret, ProposalScenario scenario, char triggerCharacter, ref bool value)
            {
                bool disabled = File.Exists(Path.Combine(Path.GetTempPath(), "ProposalsSpike.nomanager"));
                bool marker = SpikeScenarios.LineHasMarker(caret);
                if (!disabled && marker)
                {
                    value = true;
                    SpikeLog.Write("MANAGER", $"TryGetIsProposalPosition scenario={scenario} trigger={SpikeLog.Show(triggerCharacter)} pos={caret.Position.Position} virtual={caret.VirtualSpaces} -> handled, value=true");
                    return true;
                }

                value = false;
                SpikeLog.Write("MANAGER", $"TryGetIsProposalPosition scenario={scenario} trigger={SpikeLog.Show(triggerCharacter)} pos={caret.Position.Position} -> defer (marker={marker} disabled={disabled})");
                return false;
            }
        }
    }
}
