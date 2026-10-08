using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace ProposalsSpike
{
    internal sealed class SpikeProposalSource : ProposalSourceBase
    {
        private readonly ITextView view;
        private int requestCount;

        public SpikeProposalSource(ITextView view)
        {
            this.view = view;
        }

        public override Task<ProposalCollectionBase> RequestProposalsAsync(
            VirtualSnapshotPoint caret,
            CompletionState completionState,
            ProposalScenario scenario,
            char triggeringCharacter,
            CancellationToken cancel)
        {
            int n = Interlocked.Increment(ref requestCount);
            try
            {
                ITextSnapshotLine line = caret.Position.GetContainingLine();
                int col = caret.Position - line.Start;
                string before = line.Snapshot.GetText(line.Start, col);
                string after = line.Snapshot.GetText(caret.Position, line.End - caret.Position);
                string cs = completionState == null
                    ? "null"
                    : $"selected={SpikeLog.Show(completionState.SelectedItem)} applicableTo={completionState.ApplicableToSpan.Span} soft={completionState.IsSoftSelection}";

                SpikeLog.Write("REQUEST", $"#{n} scenario={scenario} trigger={SpikeLog.Show(triggeringCharacter)} caret=L{line.LineNumber + 1}:C{col + 1} pos={caret.Position.Position} virtual={caret.IsInVirtualSpace}/{caret.VirtualSpaces} v{caret.Position.Snapshot.Version.VersionNumber} completion={cs} before={SpikeLog.Show(before)} after={SpikeLog.Show(after)}");

                string newLine = line.LineBreakLength > 0 ? line.GetLineBreakText() : view.Options.GetOptionValue(DefaultOptions.NewLineCharacterOptionId);
                var result = SpikeScenarios.Build(caret, completionState, newLine);
                if (result.Note != null)
                {
                    SpikeLog.Write("SOURCE", $"#{n} note: {result.Note}");
                }

                if (result.Proposals.Count == 0)
                {
                    SpikeLog.Write("RESULT", $"#{n} scenario={result.Scenario ?? "none"} -> null");
                    return Task.FromResult<ProposalCollectionBase>(null);
                }

                foreach (var p in result.Proposals)
                {
                    SpikeLog.Write("RESULT", $"#{n} scenario={result.Scenario} -> {SpikeProposalSourceProvider.Describe(p)}");
                }

                return Task.FromResult<ProposalCollectionBase>(new ProposalCollection(SpikeProposalSourceProvider.SourceName, result.Proposals));
            }
            catch (Exception ex)
            {
                SpikeLog.Write("ERROR", $"#{n} {ex}");
                return Task.FromResult<ProposalCollectionBase>(null);
            }
        }

        public override Task DisposeAsync()
        {
            SpikeLog.Write("SOURCE", $"DisposeAsync after {requestCount} requests");
            return base.DisposeAsync();
        }
    }
}
