using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;

namespace Pillion.VisualStudio.InlineChat;

/// <summary>The editor services inline chat uses, imported once through MEF.</summary>
[Export(typeof(InlineChatEditorServices))]
internal sealed class InlineChatEditorServices
{
    /// <summary>The name of the adornment layer that holds the inline chat control, above the text.</summary>
    public const string AdornmentLayer = "PillionInlineChat";

    /// <summary>The name of the adornment layer that highlights the scope, below the text.</summary>
    public const string ScopeLayer = "PillionInlineChatScope";

    /// <summary>The text view role of Pillion's inline difference view, so editor parts can tell it apart from document views.</summary>
    public const string DiffViewRole = "PILLIONINLINEDIFF";

    // MEF exports the two layer definitions from these fields; the editor reads them, this code never assigns them.
#pragma warning disable CS0649, CS0169, IDE0044

    /// <summary>Defines the adornment layer of the control (exported for the editor; never read).</summary>
    [Export(typeof(AdornmentLayerDefinition))]
    [Name(AdornmentLayer)]
    [Order(After = PredefinedAdornmentLayers.Text)]
    [Order(After = PredefinedAdornmentLayers.Caret)]
    internal AdornmentLayerDefinition? ControlLayerDefinition;

    /// <summary>Defines the adornment layer of the scope highlight (exported for the editor; never read).</summary>
    [Export(typeof(AdornmentLayerDefinition))]
    [Name(ScopeLayer)]
    [Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
    internal AdornmentLayerDefinition? ScopeLayerDefinition;
#pragma warning restore CS0649, CS0169, IDE0044

    /// <summary>Initializes a new instance of the <see cref="InlineChatEditorServices"/> class.</summary>
    /// <param name="adapters">Maps legacy text views to WPF views.</param>
    /// <param name="buffers">Creates the difference view's buffers.</param>
    /// <param name="differenceBuffers">Creates difference buffers.</param>
    /// <param name="differenceViewers">Creates difference viewers.</param>
    /// <param name="editors">Creates text view role sets.</param>
    /// <param name="undoHistories">The undo histories of text buffers.</param>
    /// <param name="documents">Maps buffers to their files.</param>
    /// <param name="formatMaps">The editor's colours.</param>
    /// <param name="editorOptions">Creates editor options.</param>
    [ImportingConstructor]
    public InlineChatEditorServices(
        IVsEditorAdaptersFactoryService adapters,
        ITextBufferFactoryService buffers,
        IDifferenceBufferFactoryService differenceBuffers,
        IWpfDifferenceViewerFactoryService differenceViewers,
        ITextEditorFactoryService editors,
        ITextUndoHistoryRegistry undoHistories,
        ITextDocumentFactoryService documents,
        IEditorFormatMapService formatMaps,
        IEditorOptionsFactoryService editorOptions)
    {
        Adapters = adapters;
        Buffers = buffers;
        DifferenceBuffers = differenceBuffers;
        DifferenceViewers = differenceViewers;
        Editors = editors;
        UndoHistories = undoHistories;
        Documents = documents;
        FormatMaps = formatMaps;
        EditorOptions = editorOptions;
    }

    /// <summary>Gets the adapter service.</summary>
    public IVsEditorAdaptersFactoryService Adapters { get; }

    /// <summary>Gets the text buffer factory.</summary>
    public ITextBufferFactoryService Buffers { get; }

    /// <summary>Gets the difference buffer factory.</summary>
    public IDifferenceBufferFactoryService DifferenceBuffers { get; }

    /// <summary>Gets the difference viewer factory.</summary>
    public IWpfDifferenceViewerFactoryService DifferenceViewers { get; }

    /// <summary>Gets the text editor factory.</summary>
    public ITextEditorFactoryService Editors { get; }

    /// <summary>Gets the undo history registry.</summary>
    public ITextUndoHistoryRegistry UndoHistories { get; }

    /// <summary>Gets the text document factory.</summary>
    public ITextDocumentFactoryService Documents { get; }

    /// <summary>Gets the format map service.</summary>
    public IEditorFormatMapService FormatMaps { get; }

    /// <summary>Gets the editor options factory.</summary>
    public IEditorOptionsFactoryService EditorOptions { get; }
}
