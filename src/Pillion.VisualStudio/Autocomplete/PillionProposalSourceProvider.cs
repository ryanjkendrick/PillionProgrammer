using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace Pillion.VisualStudio.Autocomplete;

/// <summary>
/// The MEF entry point of Pillion's ghost text. Visual Studio's proposal broker imports every
/// <see cref="ProposalSourceProviderBase"/> with name, order and content-type metadata; text view roles are not part
/// of that metadata, so <see cref="AutocompleteHost.IsDocumentView"/> checks them in code. Ordered ahead of Copilot
/// (<c>CompletionsProposalSourceProvider</c>) and IntelliCode, as in the Gate 0 spike; the coexistence policy is work
/// item 1.4.
/// </summary>
[Export(typeof(ProposalSourceProviderBase))]
[Name(Name)]
[Order(Before = "CompletionsProposalSourceProvider")]
[Order(Before = ProposalSourceProviders.IntelliCodeCSharpProposalSource)]
[Order(Before = "Highest Priority")]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class PillionProposalSourceProvider : ProposalSourceProviderBase
{
    /// <summary>The provider name.</summary>
    public const string Name = "PillionProgrammer.ProposalSource";

    private readonly AutocompleteHost _host;

    /// <summary>Initializes a new instance of the <see cref="PillionProposalSourceProvider"/> class.</summary>
    /// <param name="host">The shared autocomplete host.</param>
    [ImportingConstructor]
    public PillionProposalSourceProvider(AutocompleteHost host)
    {
        _host = host;
    }

    /// <inheritdoc/>
    public override Task<ProposalSourceBase?> GetProposalSourceAsync(ITextView view, CancellationToken cancel)
    {
        if (!AutocompleteHost.IsDocumentView(view))
        {
            return Task.FromResult<ProposalSourceBase?>(null);
        }

        return Task.FromResult<ProposalSourceBase?>(new Source(_host.GetOrCreate(view)));
    }

    /// <summary>The proposal source of one view. Called on thread-pool threads on every keystroke and caret move.</summary>
    private sealed class Source(ViewAutocomplete state) : ProposalSourceBase
    {
        public override Task<ProposalCollectionBase?> RequestProposalsAsync(
            VirtualSnapshotPoint caret,
            CompletionState? completionState,
            ProposalScenario scenario,
            char triggeringCharacter,
            CancellationToken cancel) =>
            state.RequestAsync(caret, completionState, scenario, cancel);
    }
}

/// <summary>
/// Pillion's proposal manager: says "propose here" when Pillion wants to (feature on, document not suppressed) and
/// otherwise defers to the next manager, so Copilot's and IntelliCode's own decisions still apply. It runs on the UI
/// thread for every keystroke, so it only reads cached state. The decision method takes <c>ref bool</c>: returning
/// <see langword="true"/> means "decided, use the value".
/// </summary>
[Export(typeof(ProposalManagerProviderBase))]
[Name("PillionProgrammer.ProposalManager")]
[Order(Before = "CompletionsProposalManagerProvider")]
[Order(Before = ProposalManagerProviders.IntelliCodeCSharpProposalManager)]
[Order(Before = "Highest Priority")]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class PillionProposalManagerProvider : ProposalManagerProviderBase
{
    private readonly AutocompleteHost _host;

    /// <summary>Initializes a new instance of the <see cref="PillionProposalManagerProvider"/> class.</summary>
    /// <param name="host">The shared autocomplete host.</param>
    [ImportingConstructor]
    public PillionProposalManagerProvider(AutocompleteHost host)
    {
        _host = host;
    }

    /// <inheritdoc/>
    public override Task<ProposalManagerBase?> GetProposalManagerAsync(ITextView view, CancellationToken cancel)
    {
        if (!AutocompleteHost.IsDocumentView(view))
        {
            return Task.FromResult<ProposalManagerBase?>(null);
        }

        return Task.FromResult<ProposalManagerBase?>(new Manager(_host.GetOrCreate(view)));
    }

    private sealed class Manager(ViewAutocomplete state) : ProposalManagerBase
    {
        public override bool TryGetIsProposalPosition(VirtualSnapshotPoint caret, ProposalScenario scenario, char triggerCharacter, ref bool value)
        {
            if (!state.WantsProposals(caret))
            {
                return false;
            }

            value = true;
            return true;
        }
    }
}
