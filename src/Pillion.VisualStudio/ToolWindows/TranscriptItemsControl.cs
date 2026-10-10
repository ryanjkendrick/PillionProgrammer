using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Pillion.VisualStudio.Core;

namespace Pillion.VisualStudio.ToolWindows;

/// <summary>
/// The chat transcript: a virtualising <see cref="ItemsControl"/> that screen readers and UI Automation see as a list of
/// turns (a plain ItemsControl has no automation peer, and the scroll viewer in its template is hidden from the control
/// view, so the transcript would otherwise have no element of its own and no scroll pattern).
/// </summary>
internal sealed class TranscriptItemsControl : ItemsControl
{
    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer() => new TranscriptAutomationPeer(this);

    /// <summary>A list whose items are the turns; it scrolls through the template's scroll viewer.</summary>
    private sealed class TranscriptAutomationPeer : ItemsControlAutomationPeer
    {
        public TranscriptAutomationPeer(TranscriptItemsControl owner)
            : base(owner)
        {
        }

        protected override ItemAutomationPeer CreateItemAutomationPeer(object item) => new TurnAutomationPeer(item, this);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

        protected override string GetClassNameCore() => nameof(TranscriptItemsControl);
    }

    /// <summary>One turn, named like the turn itself ("You said: ...", "Reply from ...").</summary>
    private sealed class TurnAutomationPeer : ItemAutomationPeer
    {
        public TurnAutomationPeer(object item, ItemsControlAutomationPeer owner)
            : base(item, owner)
        {
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

        protected override string GetClassNameCore() => "ChatTurn";

        protected override string GetNameCore() => (Item as ChatTurnViewModel)?.AutomationName ?? base.GetNameCore();
    }
}
