using System;
using System.Collections.Generic;
using System.Windows;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Markdown;

namespace Pillion.VisualStudio.ToolWindows;

/// <summary>
/// Shows fenced code blocks in read-only Visual Studio editor views (design doc section 5), so they get the editor's
/// font, theme and native colourisation. The content type comes from the fence's language tag: the language's
/// well-known content type names first, then the content type registered for its file extension, then plain text.
/// When a view cannot be created the block falls back to the monospace text box.
/// </summary>
internal sealed class EditorCodeViewFactory : ICodeViewFactory
{
    /// <summary>The text view role of chat code views, so other extensions can recognise (and ignore) them.</summary>
    public const string TextViewRole = "PillionChatCode";

    private readonly ITextEditorFactoryService _editors;
    private readonly ITextBufferFactoryService _buffers;
    private readonly IContentTypeRegistryService _contentTypes;
    private readonly IFileExtensionRegistryService _extensions;
    private readonly ICodeViewFactory _fallback;
    private readonly PillionLog? _log;
    private readonly Dictionary<CodeLanguage, IContentType> _resolved = [];
    private bool _loggedFailure;

    /// <summary>Initializes a new instance of the <see cref="EditorCodeViewFactory"/> class.</summary>
    /// <param name="editors">The editor factory.</param>
    /// <param name="buffers">The text buffer factory.</param>
    /// <param name="contentTypes">The content type registry.</param>
    /// <param name="extensions">The file extension registry.</param>
    /// <param name="fallback">The factory used when an editor view cannot be created.</param>
    /// <param name="log">The extension log.</param>
    public EditorCodeViewFactory(
        ITextEditorFactoryService editors,
        ITextBufferFactoryService buffers,
        IContentTypeRegistryService contentTypes,
        IFileExtensionRegistryService extensions,
        ICodeViewFactory fallback,
        PillionLog? log)
    {
        _editors = editors;
        _buffers = buffers;
        _contentTypes = contentTypes;
        _extensions = extensions;
        _fallback = fallback;
        _log = log;
    }

    /// <inheritdoc/>
    public ICodeView Create(MarkdownCodeBlock block)
    {
        try
        {
            return new EditorCodeView(this, block, ContentTypeFor(block.Language));
        }
#pragma warning disable CA1031 // A code block must always render; the text box is the documented fallback.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                _log?.Warning("chat.codeview.failed", "Creating an editor view for a code block failed; using a text box.", ("error", ex.GetType().Name), ("detail", ex.Message));
            }

            return _fallback.Create(block);
        }
    }

    /// <summary>Resolves (once per language) the content type of a language's editor view.</summary>
    /// <param name="language">The language.</param>
    /// <returns>The content type.</returns>
    public IContentType ContentTypeFor(CodeLanguage language)
    {
        if (_resolved.TryGetValue(language, out var known))
        {
            return known;
        }

        IContentType? type = null;
        foreach (var name in language.ContentTypes)
        {
            type = _contentTypes.GetContentType(name);
            if (type is not null)
            {
                break;
            }
        }

        if (type is null && language.FileExtension.Length > 0)
        {
            var byExtension = _extensions.GetContentTypeForExtension(language.FileExtension.TrimStart('.'));
            if (byExtension is not null && !byExtension.IsOfType("UNKNOWN"))
            {
                type = byExtension;
            }
        }

        type ??= _contentTypes.GetContentType(CodeLanguage.PlainTextContentType);
        _resolved[language] = type;
        _log?.Debug("chat.codeview.content_type", "Chat code blocks in " + language.Id + " use content type " + type.TypeName + ".");
        return type;
    }

    private sealed class EditorCodeView : ICodeView
    {
        /// <summary>Room for the view's horizontal scroll bar and a little padding, in device-independent pixels.</summary>
        private const double ChromeHeight = 22;

        private readonly ITextBuffer _buffer;
        private readonly IWpfTextView _view;
        private readonly IWpfTextViewHost _host;
        private string _code;

        public EditorCodeView(EditorCodeViewFactory factory, MarkdownCodeBlock block, IContentType contentType)
        {
            _code = block.Code;
            _buffer = factory._buffers.CreateTextBuffer(block.Code, contentType);
            var roles = factory._editors.CreateTextViewRoleSet(PredefinedTextViewRoles.Interactive, TextViewRole);
            _view = factory._editors.CreateTextView(_buffer, roles);
            var options = _view.Options;
            options.SetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId, true);
            options.SetOptionValue(DefaultTextViewOptions.WordWrapStyleId, WordWrapStyles.None);
            options.SetOptionValue(DefaultTextViewOptions.DisplayUrlsAsHyperlinksId, false); // Links open only after confirmation.
            options.SetOptionValue(DefaultTextViewOptions.DragDropEditingId, false);
            options.SetOptionValue(DefaultTextViewOptions.ShowBlockStructureId, false);
            options.SetOptionValue(DefaultTextViewHostOptions.LineNumberMarginId, false);
            options.SetOptionValue(DefaultTextViewHostOptions.GlyphMarginId, false);
            options.SetOptionValue(DefaultTextViewHostOptions.SelectionMarginId, false);
            options.SetOptionValue(DefaultTextViewHostOptions.OutliningMarginId, false);
            options.SetOptionValue(DefaultTextViewHostOptions.ChangeTrackingId, false);
            options.SetOptionValue(DefaultTextViewHostOptions.ZoomControlId, false);
            options.SetOptionValue(DefaultTextViewHostOptions.VerticalScrollBarId, false);
            options.SetOptionValue(DefaultTextViewHostOptions.HorizontalScrollBarId, true);
            options.SetOptionValue(DefaultWpfViewOptions.EnableHighlightCurrentLineId, false);
            _host = factory._editors.CreateTextViewHost(_view, setFocus: false);
            _host.HostControl.ToolTip = ChatStrings.Format(ChatStrings.CodeBlockNameFormat, block.Language.NameFor(block.Info));
            AutomationPropertiesHelper.SetName(_host.HostControl, ChatStrings.Format(ChatStrings.CodeBlockNameFormat, block.Language.NameFor(block.Info)));
            UpdateHeight();
        }

        public FrameworkElement Element => _host.HostControl;

        public void Update(MarkdownCodeBlock block)
        {
            var code = block.Code;
            if (string.Equals(code, _code, StringComparison.Ordinal))
            {
                return;
            }

            // While a reply streams the code only grows: append the new text rather than replacing the buffer.
            var snapshot = _buffer.CurrentSnapshot;
            if (code.StartsWith(_code, StringComparison.Ordinal) && snapshot.Length == _code.Length)
            {
                _buffer.Insert(snapshot.Length, code.Substring(_code.Length));
            }
            else
            {
                _buffer.Replace(new Span(0, snapshot.Length), code);
            }

            _code = code;
            UpdateHeight();
        }

        public void Dispose()
        {
            if (!_host.IsClosed)
            {
                _host.Close();
            }
        }

        /// <summary>Sizes the view to its lines, so the transcript (not the view) scrolls.</summary>
        private void UpdateHeight()
        {
            var lines = Math.Max(1, _buffer.CurrentSnapshot.LineCount);
            var lineHeight = _view.LineHeight > 0 ? _view.LineHeight : 16;
            _host.HostControl.Height = (lines * lineHeight) + ChromeHeight;
        }
    }
}
