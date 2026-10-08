using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Language.Suggestions;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace ProposalsSpike
{
    /// <summary>
    /// MEF entry point. VS's ProposalBrokerProvider (Microsoft.VisualStudio.Editor.Implementation) imports
    /// <c>IEnumerable&lt;Lazy&lt;ProposalSourceProviderBase, IProposalMetadata&gt;&gt;</c> where IProposalMetadata is
    /// IOrderable (Name/Before/After) + IContentTypeMetadata (ContentTypes) + optional OptionName (DeferCreation).
    /// TextViewRole is NOT part of that metadata view, so it is declared here for documentation only and the
    /// role is re-checked in code.
    /// </summary>
    [Export(typeof(ProposalSourceProviderBase))]
    [Name(SourceName)]
    // Copilot's provider is [Order(Before = "InlineCSharpProposalSourceProvider")] + [Order(Before = "Highest Priority")].
    // Ask to be ahead of both Copilot and IntelliCode so our canned proposals win while the spike runs.
    [Order(Before = "CompletionsProposalSourceProvider")]
    [Order(Before = ProposalSourceProviders.IntelliCodeCSharpProposalSource)]
    [Order(Before = "Highest Priority")]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class SpikeProposalSourceProvider : ProposalSourceProviderBase
    {
        public const string SourceName = "PillionProgrammer.ProposalsSpikeSource";

        private static int subscribed;

        [ImportingConstructor]
        public SpikeProposalSourceProvider([Import(AllowDefault = true)] SuggestionServiceBase suggestionService)
        {
            SpikeLog.Write("MEF", $"SpikeProposalSourceProvider created; SuggestionServiceBase={(suggestionService == null ? "<none>" : suggestionService.GetType().FullName)}; VS process={System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileVersionInfo.FileVersion}");
            if (suggestionService != null && Interlocked.Exchange(ref subscribed, 1) == 0)
            {
                suggestionService.ProposalDisplayed += (s, e) =>
                    SpikeLog.Write("EVENT", $"ProposalDisplayed provider={e.ProviderName} {Describe(e.OriginalProposal)}");
                suggestionService.ProposalRejected += (s, e) =>
                    SpikeLog.Write("EVENT", $"ProposalRejected provider={e.ProviderName} reason={e.Reason} {Describe(e.OriginalProposal)}");
                suggestionService.SuggestionAccepted += (s, e) =>
                    SpikeLog.Write("EVENT", $"SuggestionAccepted provider={e.ProviderName} reason={e.Reason} {Describe(e.FinalProposal ?? e.OriginalProposal)}");
                suggestionService.SuggestionDismissed += (s, e) =>
                    SpikeLog.Write("EVENT", $"SuggestionDismissed provider={e.ProviderName} reason={e.Reason} {Describe(e.FinalProposal ?? e.OriginalProposal)}");
            }
        }

        public override Task<ProposalSourceBase> GetProposalSourceAsync(ITextView view, CancellationToken cancel)
        {
            string roles = string.Join(",", view.Roles);
            bool isDocument = view.Roles.Contains(PredefinedTextViewRoles.Document);
            SpikeLog.Write("PROVIDER", $"GetProposalSourceAsync contentType={view.TextBuffer.ContentType.TypeName} roles=[{roles}] document={isDocument}");
            if (!isDocument)
            {
                return Task.FromResult<ProposalSourceBase>(null);
            }

            return Task.FromResult<ProposalSourceBase>(new SpikeProposalSource(view));
        }

        internal static string Describe(ProposalBase p)
        {
            if (p == null) return "proposal=<null>";
            bool ours = p.ProposalId != null && p.ProposalId.StartsWith("spike:", StringComparison.Ordinal);
            string edits = p.Edits == null ? "" : string.Join(" | ", p.Edits.Select(x => $"{x.Span.Span}->{SpikeLog.Show(x.ReplacementText)}"));
            return $"ours={ours} id={p.ProposalId ?? "<null>"} type={p.GetType().Name} flags={p.Flags} edits=[{edits}]";
        }
    }
}
