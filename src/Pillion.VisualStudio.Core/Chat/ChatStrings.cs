using System.Globalization;
using System.Resources;

namespace Pillion.VisualStudio.Core;

/// <summary>
/// The chat window's user-facing strings, from <c>Chat/ChatStrings.resx</c> (design doc section 5, Localisation). The XAML
/// binds to these with <c>x:Static</c>; translations go in satellite files such as <c>ChatStrings.de.resx</c>.
/// </summary>
public static class ChatStrings
{
    /// <summary>The manifest name of <c>Chat/ChatStrings.resx</c>: the root namespace plus the folder.</summary>
    private const string ResourceName = "Pillion.VisualStudio.Core.Chat.ChatStrings";

    private static readonly ResourceManager Resources = new(ResourceName, typeof(ChatStrings).Assembly);

    /// <summary>Gets the label of the conversation list ("_Conversation:").</summary>
    public static string ConversationLabel => Get(nameof(ConversationLabel));

    /// <summary>Gets the screen-reader name of the conversation list.</summary>
    public static string ConversationListName => Get(nameof(ConversationListName));

    /// <summary>Gets the title of a conversation before its first message.</summary>
    public static string NewConversationTitle => Get(nameof(NewConversationTitle));

    /// <summary>Gets the New conversation button's text.</summary>
    public static string NewConversationButton => Get(nameof(NewConversationButton));

    /// <summary>Gets the New conversation button's screen-reader name.</summary>
    public static string NewConversationName => Get(nameof(NewConversationName));

    /// <summary>Gets the New conversation button's tooltip.</summary>
    public static string NewConversationToolTip => Get(nameof(NewConversationToolTip));

    /// <summary>Gets the Rename button's text.</summary>
    public static string RenameConversationButton => Get(nameof(RenameConversationButton));

    /// <summary>Gets the Rename button's screen-reader name.</summary>
    public static string RenameConversationName => Get(nameof(RenameConversationName));

    /// <summary>Gets the Rename button's tooltip.</summary>
    public static string RenameConversationToolTip => Get(nameof(RenameConversationToolTip));

    /// <summary>Gets the screen-reader name of the box that edits a conversation's name.</summary>
    public static string RenameBoxName => Get(nameof(RenameBoxName));

    /// <summary>Gets the help text of the box that edits a conversation's name.</summary>
    public static string RenameBoxHelp => Get(nameof(RenameBoxHelp));

    /// <summary>Gets the Delete button's text.</summary>
    public static string DeleteConversationButton => Get(nameof(DeleteConversationButton));

    /// <summary>Gets the Delete button's screen-reader name.</summary>
    public static string DeleteConversationName => Get(nameof(DeleteConversationName));

    /// <summary>Gets the Delete button's tooltip.</summary>
    public static string DeleteConversationToolTip => Get(nameof(DeleteConversationToolTip));

    /// <summary>Gets the delete confirmation; {0} is the conversation's title.</summary>
    public static string DeleteConversationConfirmFormat => Get(nameof(DeleteConversationConfirmFormat));

    /// <summary>Gets a conversation's screen-reader name; {0} is the title, {1} the number of messages.</summary>
    public static string ConversationItemNameFormat => Get(nameof(ConversationItemNameFormat));

    /// <summary>Gets the name shown for a code block without a known language.</summary>
    public static string PlainTextLanguage => Get(nameof(PlainTextLanguage));

    /// <summary>Gets a code block's tooltip and screen-reader name; {0} is the language.</summary>
    public static string CodeBlockNameFormat => Get(nameof(CodeBlockNameFormat));

    /// <summary>Gets the Copy button's text.</summary>
    public static string CopyCodeButton => Get(nameof(CopyCodeButton));

    /// <summary>Gets the Copy button's screen-reader name; {0} is the language.</summary>
    public static string CopyCodeNameFormat => Get(nameof(CopyCodeNameFormat));

    /// <summary>Gets the Copy button's tooltip.</summary>
    public static string CopyCodeToolTip => Get(nameof(CopyCodeToolTip));

    /// <summary>Gets the note shown after Copy.</summary>
    public static string CodeCopied => Get(nameof(CodeCopied));

    /// <summary>Gets the note shown when the clipboard could not be written.</summary>
    public static string CopyFailed => Get(nameof(CopyFailed));

    /// <summary>Gets the Insert at cursor button's text.</summary>
    public static string InsertCodeButton => Get(nameof(InsertCodeButton));

    /// <summary>Gets the Insert at cursor button's screen-reader name; {0} is the language.</summary>
    public static string InsertCodeNameFormat => Get(nameof(InsertCodeNameFormat));

    /// <summary>Gets the Insert at cursor button's tooltip.</summary>
    public static string InsertCodeToolTip => Get(nameof(InsertCodeToolTip));

    /// <summary>Gets the note shown after Insert at cursor; {0} is the document's file name.</summary>
    public static string CodeInsertedFormat => Get(nameof(CodeInsertedFormat));

    /// <summary>Gets the name used in <see cref="CodeInsertedFormat"/> when the document has no file name.</summary>
    public static string ActiveDocumentName => Get(nameof(ActiveDocumentName));

    /// <summary>Gets the note shown when no document is open to insert into.</summary>
    public static string InsertNoDocument => Get(nameof(InsertNoDocument));

    /// <summary>Gets the note shown when the active document cannot be edited.</summary>
    public static string InsertReadOnly => Get(nameof(InsertReadOnly));

    /// <summary>Gets the title of the open-link confirmation.</summary>
    public static string OpenLinkTitle => Get(nameof(OpenLinkTitle));

    /// <summary>Gets the open-link confirmation; {0} is the address.</summary>
    public static string OpenLinkConfirmFormat => Get(nameof(OpenLinkConfirmFormat));

    /// <summary>Gets the line added to the confirmation for an internationalised host; {0} is its ASCII form.</summary>
    public static string LinkAsciiHostFormat => Get(nameof(LinkAsciiHostFormat));

    /// <summary>Gets a link's tooltip; {0} is the address.</summary>
    public static string LinkToolTipFormat => Get(nameof(LinkToolTipFormat));

    /// <summary>Gets the tooltip of a link that is never opened; {0} is the address as written.</summary>
    public static string UnsafeLinkToolTipFormat => Get(nameof(UnsafeLinkToolTipFormat));

    /// <summary>Gets the text shown for an image; {0} is its alt text.</summary>
    public static string ImageAltFormat => Get(nameof(ImageAltFormat));

    /// <summary>Gets the text shown for an image without alt text.</summary>
    public static string ImageWithoutAlt => Get(nameof(ImageWithoutAlt));

    /// <summary>Gets an image's tooltip; {0} is the image address.</summary>
    public static string ImageToolTipFormat => Get(nameof(ImageToolTipFormat));

    /// <summary>Gets a heading's screen-reader name; {0} is the level, {1} the text.</summary>
    public static string HeadingNameFormat => Get(nameof(HeadingNameFormat));

    /// <summary>Gets a list's screen-reader name; {0} is the number of items.</summary>
    public static string ListNameFormat => Get(nameof(ListNameFormat));

    /// <summary>Gets a table's screen-reader name; {0} is the number of rows, {1} of columns.</summary>
    public static string TableNameFormat => Get(nameof(TableNameFormat));

    /// <summary>Gets a block quote's screen-reader name.</summary>
    public static string QuoteName => Get(nameof(QuoteName));

    /// <summary>Gets a thematic break's screen-reader name.</summary>
    public static string RuleName => Get(nameof(RuleName));

    /// <summary>Gets a reply's screen-reader name for its rendered content.</summary>
    public static string ReplyContentName => Get(nameof(ReplyContentName));

    /// <summary>Formats a string from this file with the current culture.</summary>
    /// <param name="format">The format (one of the <c>...Format</c> properties).</param>
    /// <param name="args">The arguments.</param>
    /// <returns>The formatted text.</returns>
    public static string Format(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    private static string Get(string name) => Resources.GetString(name, CultureInfo.CurrentUICulture) ?? name;
}
