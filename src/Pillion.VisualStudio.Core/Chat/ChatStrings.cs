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

    /// <summary>Gets the string: Screen-reader name of the mention picker that opens at the caret after @.</summary>
    public static string MentionPickerName => Get(nameof(MentionPickerName));

    /// <summary>Gets the string: Title of the mention picker right after @ is typed.</summary>
    public static string MentionPickerHint => Get(nameof(MentionPickerHint));

    /// <summary>Gets the string: Title of the mention picker while it lists matches.</summary>
    public static string MentionPickerMatchesTitle => Get(nameof(MentionPickerMatchesTitle));

    /// <summary>Gets the string: Title of the mention picker after @file: is typed.</summary>
    public static string MentionPickerFileHint => Get(nameof(MentionPickerFileHint));

    /// <summary>Gets the string: Title of the mention picker after @folder: is typed.</summary>
    public static string MentionPickerFolderHint => Get(nameof(MentionPickerFolderHint));

    /// <summary>Gets the string: Title of the mention picker when nothing matches.</summary>
    public static string MentionPickerNoMatches => Get(nameof(MentionPickerNoMatches));

    /// <summary>Gets the string: Title of the sub-picker for an ambiguous mention; {0} is the mention.</summary>
    public static string MentionCandidatesTitleFormat => Get(nameof(MentionCandidatesTitleFormat));

    /// <summary>Gets the string: Description of @file in the picker.</summary>
    public static string MentionKindFile => Get(nameof(MentionKindFile));

    /// <summary>Gets the string: Description of @folder in the picker.</summary>
    public static string MentionKindFolder => Get(nameof(MentionKindFolder));

    /// <summary>Gets the string: Description of @selection in the picker.</summary>
    public static string MentionKindSelection => Get(nameof(MentionKindSelection));

    /// <summary>Gets the string: Description of @errors in the picker.</summary>
    public static string MentionKindErrors => Get(nameof(MentionKindErrors));

    /// <summary>Gets the string: Description of @clipboard in the picker.</summary>
    public static string MentionKindClipboard => Get(nameof(MentionKindClipboard));

    /// <summary>Gets the string: Shown under the input when resolving a mention failed without a reason.</summary>
    public static string MentionFailed => Get(nameof(MentionFailed));

    /// <summary>Gets the string: Shown under the input when a mention did not resolve and the host gave no reason; {0} is the mention.</summary>
    public static string MentionUnresolvedFormat => Get(nameof(MentionUnresolvedFormat));

    /// <summary>Gets the string: Screen-reader name and tooltip of the button that hides the mention notice.</summary>
    public static string DismissNoticeName => Get(nameof(DismissNoticeName));

    /// <summary>Gets the string: Screen-reader name of the row of pins above the input.</summary>
    public static string PinsListName => Get(nameof(PinsListName));

    /// <summary>Gets the string: Pin tooltip line; {0} is the token count.</summary>
    public static string PinTokensFormat => Get(nameof(PinTokensFormat));

    /// <summary>Gets the string: Pin tooltip line for a pin whose content was cut.</summary>
    public static string PinTruncated => Get(nameof(PinTruncated));

    /// <summary>Gets the string: Pin tooltip line for a sensitive pin.</summary>
    public static string PinSensitive => Get(nameof(PinSensitive));

    /// <summary>Gets the string: Added to a sensitive pin's screen-reader name.</summary>
    public static string PinSensitiveShort => Get(nameof(PinSensitiveShort));

    /// <summary>Gets the string: Screen-reader name of a pin; {0} is its name, {1} the token count.</summary>
    public static string PinAutomationNameFormat => Get(nameof(PinAutomationNameFormat));

    /// <summary>Gets the string: Screen-reader name of a pin's remove button; {0} is its name.</summary>
    public static string RemovePinNameFormat => Get(nameof(RemovePinNameFormat));

    /// <summary>Gets the string: Tooltip of a pin's remove button.</summary>
    public static string RemovePinToolTip => Get(nameof(RemovePinToolTip));

    /// <summary>Gets the string: Screen-reader name of the lock glyph on a sensitive pin.</summary>
    public static string SensitiveGlyphName => Get(nameof(SensitiveGlyphName));

    /// <summary>Gets the string: Approval prompt for an excluded or secret file; {0} is the file name.</summary>
    public static string PinApprovalPromptFormat => Get(nameof(PinApprovalPromptFormat));

    /// <summary>Gets the string: Button that pins the file waiting for approval.</summary>
    public static string PinApproveButton => Get(nameof(PinApproveButton));

    /// <summary>Gets the string: Button that leaves the file waiting for approval unpinned.</summary>
    public static string PinDeclineButton => Get(nameof(PinDeclineButton));

    /// <summary>Gets the string: Check box: send the active document's path and region with each message.</summary>
    public static string IncludeCurrentFileLabel => Get(nameof(IncludeCurrentFileLabel));

    /// <summary>Gets the string: Help text of the Include current file check box.</summary>
    public static string IncludeCurrentFileHelp => Get(nameof(IncludeCurrentFileHelp));

    /// <summary>Gets the string: Button beside Send that opens the Context Inspector.</summary>
    public static string InspectContextButton => Get(nameof(InspectContextButton));

    /// <summary>Gets the string: Screen-reader name of the Inspect button.</summary>
    public static string InspectContextName => Get(nameof(InspectContextName));

    /// <summary>Gets the string: Tooltip of the Inspect button.</summary>
    public static string InspectContextToolTip => Get(nameof(InspectContextToolTip));

    /// <summary>Gets the string: Heading of the Context Inspector panel.</summary>
    public static string InspectorTitle => Get(nameof(InspectorTitle));

    /// <summary>Gets the string: Shown while the Context Inspector waits for the preview.</summary>
    public static string InspectorLoading => Get(nameof(InspectorLoading));

    /// <summary>Gets the string: Button in the Context Inspector that sends the message.</summary>
    public static string InspectorSendButton => Get(nameof(InspectorSendButton));

    /// <summary>Gets the string: Button that closes the Context Inspector.</summary>
    public static string InspectorCloseButton => Get(nameof(InspectorCloseButton));

    /// <summary>Gets the string: Screen-reader name of the Context Inspector's list.</summary>
    public static string InspectorListName => Get(nameof(InspectorListName));

    /// <summary>Gets the string: Shown in the Context Inspector when the preview failed without a message.</summary>
    public static string InspectorFailed => Get(nameof(InspectorFailed));

    /// <summary>Gets the string: Context Inspector summary; {0} model, {1} provider, {2} local or cloud, {3} tokens used, {4} tokens available, {5} answer reserve.</summary>
    public static string InspectorSummaryFormat => Get(nameof(InspectorSummaryFormat));

    /// <summary>Gets the string: Context Inspector note for a request that is not assembled.</summary>
    public static string InspectorPlainRequest => Get(nameof(InspectorPlainRequest));

    /// <summary>Gets the string: Context Inspector redaction notice; {0} is the count.</summary>
    public static string InspectorRedactionFormat => Get(nameof(InspectorRedactionFormat));

    /// <summary>Gets the string: Context Inspector note when redaction applies but nothing was found.</summary>
    public static string InspectorRedactionNone => Get(nameof(InspectorRedactionNone));

    /// <summary>Gets the string: Redaction notice on one Context Inspector line; {0} is the count.</summary>
    public static string InspectorItemRedactionFormat => Get(nameof(InspectorItemRedactionFormat));

    /// <summary>Gets the string: Token figure of a Context Inspector line; {0} is the count.</summary>
    public static string InspectorTokensFormat => Get(nameof(InspectorTokensFormat));

    /// <summary>Gets the string: Token figure of a cut Context Inspector line; {0} kept, {1} in full.</summary>
    public static string InspectorTruncatedTokensFormat => Get(nameof(InspectorTruncatedTokensFormat));

    /// <summary>Gets the string: Token figure of a dropped Context Inspector line; {0} is its full size.</summary>
    public static string InspectorDroppedTokensFormat => Get(nameof(InspectorDroppedTokensFormat));

    /// <summary>Gets the string: State of a cut Context Inspector line; {0} is the reason.</summary>
    public static string InspectorTruncatedFormat => Get(nameof(InspectorTruncatedFormat));

    /// <summary>Gets the string: State of a dropped Context Inspector line; {0} is the reason.</summary>
    public static string InspectorDroppedFormat => Get(nameof(InspectorDroppedFormat));

    /// <summary>Gets the string: Kind of a Context Inspector line.</summary>
    public static string InspectorKindSystem => Get(nameof(InspectorKindSystem));

    /// <summary>Gets the string: Kind of a Context Inspector line.</summary>
    public static string InspectorKindRules => Get(nameof(InspectorKindRules));

    /// <summary>Gets the string: Kind of a Context Inspector line.</summary>
    public static string InspectorKindRepoMap => Get(nameof(InspectorKindRepoMap));

    /// <summary>Gets the string: Kind of a Context Inspector line.</summary>
    public static string InspectorKindPin => Get(nameof(InspectorKindPin));

    /// <summary>Gets the string: Kind of a Context Inspector line.</summary>
    public static string InspectorKindCurrentFile => Get(nameof(InspectorKindCurrentFile));

    /// <summary>Gets the string: Kind of a Context Inspector line.</summary>
    public static string InspectorKindRetrieved => Get(nameof(InspectorKindRetrieved));

    /// <summary>Gets the string: Kind of a Context Inspector line.</summary>
    public static string InspectorKindSummary => Get(nameof(InspectorKindSummary));

    /// <summary>Gets the string: Kind of a Context Inspector line.</summary>
    public static string InspectorKindHistory => Get(nameof(InspectorKindHistory));

    /// <summary>Gets the string: Kind of a Context Inspector line.</summary>
    public static string InspectorKindQuery => Get(nameof(InspectorKindQuery));

    /// <summary>Gets the string: A model that runs on this machine.</summary>
    public static string LocalityLocal => Get(nameof(LocalityLocal));

    /// <summary>Gets the string: A model that runs in the cloud.</summary>
    public static string LocalityCloud => Get(nameof(LocalityCloud));

    /// <summary>Gets the string: Header of the Outbound log expander.</summary>
    public static string OutboundLogHeader => Get(nameof(OutboundLogHeader));

    /// <summary>Gets the string: Screen-reader name of the Outbound log list.</summary>
    public static string OutboundListName => Get(nameof(OutboundListName));

    /// <summary>Gets the string: Button that reads the Outbound log again.</summary>
    public static string OutboundRefreshButton => Get(nameof(OutboundRefreshButton));

    /// <summary>Gets the string: Screen-reader name of the Outbound log's Refresh button.</summary>
    public static string OutboundRefreshName => Get(nameof(OutboundRefreshName));

    /// <summary>Gets the string: Button that copies the Outbound log as text.</summary>
    public static string OutboundCopyButton => Get(nameof(OutboundCopyButton));

    /// <summary>Gets the string: Screen-reader name of the Outbound log's Copy button.</summary>
    public static string OutboundCopyName => Get(nameof(OutboundCopyName));

    /// <summary>Gets the string: Outbound log summary when it is empty.</summary>
    public static string OutboundEmpty => Get(nameof(OutboundEmpty));

    /// <summary>Gets the string: Outbound log summary; {0} is the number of rows.</summary>
    public static string OutboundCountFormat => Get(nameof(OutboundCountFormat));

    /// <summary>Gets the string: Shown when reading the Outbound log failed without a message.</summary>
    public static string OutboundFailed => Get(nameof(OutboundFailed));

    /// <summary>Gets the string: Tokens column of an autocomplete request served from the cache.</summary>
    public static string OutboundCacheHit => Get(nameof(OutboundCacheHit));

    /// <summary>Gets the string: Tokens column; {0} prompt tokens, {1} completion tokens.</summary>
    public static string OutboundTokensFormat => Get(nameof(OutboundTokensFormat));

    /// <summary>Gets the string: Tooltip of an assembled chat request's row; {0} items, {1} trimmed.</summary>
    public static string OutboundContextFormat => Get(nameof(OutboundContextFormat));

    /// <summary>Gets the string: Outbound log column.</summary>
    public static string OutboundColumnTime => Get(nameof(OutboundColumnTime));

    /// <summary>Gets the string: Outbound log column.</summary>
    public static string OutboundColumnTaskClass => Get(nameof(OutboundColumnTaskClass));

    /// <summary>Gets the string: Outbound log column.</summary>
    public static string OutboundColumnModel => Get(nameof(OutboundColumnModel));

    /// <summary>Gets the string: Outbound log column.</summary>
    public static string OutboundColumnLocality => Get(nameof(OutboundColumnLocality));

    /// <summary>Gets the string: Outbound log column.</summary>
    public static string OutboundColumnTokens => Get(nameof(OutboundColumnTokens));

    /// <summary>Gets the string: Outbound log column.</summary>
    public static string OutboundColumnRedactions => Get(nameof(OutboundColumnRedactions));

    /// <summary>Gets the string: Outbound log column.</summary>
    public static string OutboundColumnDuration => Get(nameof(OutboundColumnDuration));

    /// <summary>Gets the string: Outbound log column.</summary>
    public static string OutboundColumnOutcome => Get(nameof(OutboundColumnOutcome));

    /// <summary>Gets the line the tooltip of a restored pin that could not be read again ends with.</summary>
    public static string PinStale => Get(nameof(PinStale));

    /// <summary>Gets the word a screen reader adds for a restored pin that could not be read again.</summary>
    public static string PinStaleShort => Get(nameof(PinStaleShort));

    /// <summary>Gets the screen-reader name of a citation link, with the link text.</summary>
    public static string CitationNameFormat => Get(nameof(CitationNameFormat));

    /// <summary>Gets the label before the spans a reply names itself (explicit citations).</summary>
    public static string CitationsLabel => Get(nameof(CitationsLabel));

    /// <summary>Gets the collapsed line under a reply listing its implicit sources; {0} is the count.</summary>
    public static string SourcesFormat => Get(nameof(SourcesFormat));

    /// <summary>Gets the screen-reader name of the collapsed Sources line; {0} is the count.</summary>
    public static string SourcesCollapsedNameFormat => Get(nameof(SourcesCollapsedNameFormat));

    /// <summary>Gets the screen-reader name of the expanded Sources line; {0} is the count.</summary>
    public static string SourcesExpandedNameFormat => Get(nameof(SourcesExpandedNameFormat));

    /// <summary>Gets the screen-reader name of the remove button of a retrieved chunk or the repository map; {0} is its name.</summary>
    public static string RemoveContextItemNameFormat => Get(nameof(RemoveContextItemNameFormat));

    /// <summary>Gets the notice shown while saved conversations are turned off (the localStore feature).</summary>
    public static string ConversationsNotSavedOff => Get(nameof(ConversationsNotSavedOff));

    /// <summary>Gets the notice shown while the host cannot save conversations, with the reason.</summary>
    public static string ConversationsNotSavedFormat => Get(nameof(ConversationsNotSavedFormat));

    /// <summary>Gets the line shown while a saved conversation is being read.</summary>
    public static string ConversationLoadingText => Get(nameof(ConversationLoadingText));

    /// <summary>Gets the line shown when a saved conversation could not be read, with the reason.</summary>
    public static string ConversationLoadFailedFormat => Get(nameof(ConversationLoadFailedFormat));

    /// <summary>Gets the button that reads a saved conversation again.</summary>
    public static string ConversationRetryLoadButton => Get(nameof(ConversationRetryLoadButton));

    /// <summary>Gets the notice shown when a rename or delete could not reach the host, with the reason.</summary>
    public static string ConversationSaveFailedFormat => Get(nameof(ConversationSaveFailedFormat));

    /// <summary>Gets the conversation list menu item that renames a conversation.</summary>
    public static string ConversationMenuRename => Get(nameof(ConversationMenuRename));

    /// <summary>Gets the conversation list menu item that deletes a conversation.</summary>
    public static string ConversationMenuDelete => Get(nameof(ConversationMenuDelete));

    /// <summary>Gets the header of a saved reply that does not name its model.</summary>
    public static string SavedReplyHeader => Get(nameof(SavedReplyHeader));

    /// <summary>Formats a string from this file with the current culture.</summary>
    /// <param name="format">The format (one of the <c>...Format</c> properties).</param>
    /// <param name="args">The arguments.</param>
    /// <returns>The formatted text.</returns>
    public static string Format(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    private static string Get(string name) => Resources.GetString(name, CultureInfo.CurrentUICulture) ?? name;
}
