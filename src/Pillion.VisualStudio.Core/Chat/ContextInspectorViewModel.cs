using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Pillion.Contracts;
using Pillion.VisualStudio.Core.Mentions;

namespace Pillion.VisualStudio.Core;

/// <summary>One line of the Context Inspector: something that would go into the prompt, or be left out of it.</summary>
public sealed class ContextInspectorItem
{
    /// <summary>Initializes a new instance of the <see cref="ContextInspectorItem"/> class.</summary>
    /// <param name="item">The host's report line.</param>
    /// <param name="pin">The pin it shows, for a pin line; <see langword="null"/> otherwise.</param>
    public ContextInspectorItem(ContextReportItemDto item, MentionPin? pin)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        Pin = pin;
    }

    /// <summary>Gets the host's report line.</summary>
    public ContextReportItemDto Item { get; }

    /// <summary>Gets the pin the line shows (the remove button unpins it), or <see langword="null"/>.</summary>
    public MentionPin? Pin { get; }

    /// <summary>Gets a value indicating whether the line has a remove button (it is a pin).</summary>
    public bool CanRemove => Pin is not null;

    /// <summary>Gets the name to show.</summary>
    public string Name => Item.Name;

    /// <summary>Gets what the item is, in words ("Pin", "Current file", ...).</summary>
    public string KindLabel => Item.Kind switch
    {
        "system" => ChatStrings.InspectorKindSystem,
        "rules" => ChatStrings.InspectorKindRules,
        "repoMap" => ChatStrings.InspectorKindRepoMap,
        "pin" => ChatStrings.InspectorKindPin,
        "currentFile" => ChatStrings.InspectorKindCurrentFile,
        "retrieved" => ChatStrings.InspectorKindRetrieved,
        "summary" => ChatStrings.InspectorKindSummary,
        "history" => ChatStrings.InspectorKindHistory,
        "query" => ChatStrings.InspectorKindQuery,
        _ => Item.Kind,
    };

    /// <summary>Gets the token figure: the tokens it takes, of how many when it was cut, or that it was left out.</summary>
    public string TokensText => Item.Dropped
        ? ChatStrings.Format(ChatStrings.InspectorDroppedTokensFormat, Item.OriginalTokens)
        : Item.Truncated
            ? ChatStrings.Format(ChatStrings.InspectorTruncatedTokensFormat, Item.Tokens, Item.OriginalTokens)
            : ChatStrings.Format(ChatStrings.InspectorTokensFormat, Item.Tokens);

    /// <summary>Gets the state in words with the host's reason ("Cut: ...", "Left out: ..."), or empty when it goes in whole.</summary>
    public string StateText => Item.Dropped
        ? ChatStrings.Format(ChatStrings.InspectorDroppedFormat, Item.Note)
        : Item.Truncated
            ? ChatStrings.Format(ChatStrings.InspectorTruncatedFormat, Item.Note)
            : Item.Note;

    /// <summary>Gets a value indicating whether <see cref="StateText"/> has text.</summary>
    public bool HasStateText => StateText.Trim().Length > 0;

    /// <summary>Gets the redaction notice ("2 secrets will be replaced by placeholders"), or empty.</summary>
    public string RedactionText => Item.RedactionCount > 0
        ? ChatStrings.Format(ChatStrings.InspectorItemRedactionFormat, Item.RedactionCount)
        : string.Empty;

    /// <summary>Gets a value indicating whether <see cref="RedactionText"/> has text.</summary>
    public bool HasRedaction => Item.RedactionCount > 0;

    /// <summary>Gets the remove button's screen-reader name.</summary>
    public string RemoveAutomationName => ChatStrings.Format(ChatStrings.RemovePinNameFormat, Name);

    /// <summary>Gets the name a screen reader announces for the line.</summary>
    public string AutomationName =>
        string.Join(", ", new[] { KindLabel, Name, TokensText, StateText, RedactionText }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>
/// The Context Inspector (work item 1.13; design sections 7 and 12): the context a chat request would be assembled into,
/// shown before it is sent, with every item's token count, whether it would be cut or left out, the secrets that would be
/// replaced for a cloud model, and a remove button per pin. Filled by <see cref="ChatViewModel"/>; call on the UI thread.
/// </summary>
public sealed class ContextInspectorViewModel : ObservableObject
{
    private readonly Action<MentionPin> _unpin;
    private readonly Action _send;
    private readonly Func<bool> _canSend;
    private bool _isOpen;
    private bool _isLoading;
    private string _error = string.Empty;
    private string _summary = string.Empty;
    private string _redactionNotice = string.Empty;
    private string _modeNote = string.Empty;

    /// <summary>Initializes a new instance of the <see cref="ContextInspectorViewModel"/> class.</summary>
    /// <param name="unpin">Unpins a pin (and previews again).</param>
    /// <param name="send">Sends the message (and closes the inspector).</param>
    /// <param name="canSend">Whether the message can be sent now (the window's Send rules); always when <see langword="null"/>.</param>
    public ContextInspectorViewModel(Action<MentionPin> unpin, Action send, Func<bool>? canSend = null)
    {
        _unpin = unpin ?? throw new ArgumentNullException(nameof(unpin));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _canSend = canSend ?? (() => true);
        RemoveCommand = new DelegateCommand(p => Remove(p as ContextInspectorItem), p => p is ContextInspectorItem { CanRemove: true });
        SendCommand = new DelegateCommand(() => _send(), () => IsOpen && !IsLoading && _canSend());
        CloseCommand = new DelegateCommand(Close, () => IsOpen);
    }

    /// <summary>Gets the lines, in prompt order.</summary>
    public ObservableCollection<ContextInspectorItem> Items { get; } = [];

    /// <summary>Gets a value indicating whether the inspector is shown.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (SetProperty(ref _isOpen, value))
            {
                UpdateCommands();
            }
        }
    }

    /// <summary>Gets a value indicating whether the preview is being assembled.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                UpdateCommands();
            }
        }
    }

    /// <summary>Gets the error shown instead of the lines, or empty.</summary>
    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>Gets a value indicating whether <see cref="Error"/> has text.</summary>
    public bool HasError => _error.Length > 0;

    /// <summary>Gets the line at the top: the model, and the tokens used of the budget.</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>Gets the note that a plain request is sent as it is, or empty.</summary>
    public string ModeNote
    {
        get => _modeNote;
        private set
        {
            if (SetProperty(ref _modeNote, value))
            {
                OnPropertyChanged(nameof(HasModeNote));
            }
        }
    }

    /// <summary>Gets a value indicating whether <see cref="ModeNote"/> has text.</summary>
    public bool HasModeNote => _modeNote.Length > 0;

    /// <summary>Gets the redaction notice for the whole request, or empty.</summary>
    public string RedactionNotice
    {
        get => _redactionNotice;
        private set
        {
            if (SetProperty(ref _redactionNotice, value))
            {
                OnPropertyChanged(nameof(HasRedactionNotice));
            }
        }
    }

    /// <summary>Gets a value indicating whether <see cref="RedactionNotice"/> has text.</summary>
    public bool HasRedactionNotice => _redactionNotice.Length > 0;

    /// <summary>Gets the command that unpins a pin line's pin; its parameter is the <see cref="ContextInspectorItem"/>.</summary>
    public DelegateCommand RemoveCommand { get; }

    /// <summary>Gets the command that sends the message from the inspector.</summary>
    public DelegateCommand SendCommand { get; }

    /// <summary>Gets the command that closes the inspector.</summary>
    public DelegateCommand CloseCommand { get; }

    /// <summary>Opens the inspector in its loading state.</summary>
    public void BeginLoading()
    {
        IsOpen = true;
        IsLoading = true;
        Error = string.Empty;
    }

    /// <summary>Shows a preview.</summary>
    /// <param name="report">The host's report.</param>
    /// <param name="pins">The pins sent with the previewed request, in order (the report's pin indexes point into it).</param>
    public void Show(ContextReportDto report, IReadOnlyList<MentionPin> pins)
    {
        if (report is null)
        {
            throw new ArgumentNullException(nameof(report));
        }

        pins ??= [];
        Items.Clear();
        foreach (var item in report.Items ?? [])
        {
            var pin = item.Kind == "pin" && item.PinIndex >= 0 && item.PinIndex < pins.Count ? pins[item.PinIndex] : null;
            Items.Add(new ContextInspectorItem(item, pin));
        }

        var locality = report.IsLocal ? ChatStrings.LocalityLocal : ChatStrings.LocalityCloud;
        Summary = ChatStrings.Format(
            ChatStrings.InspectorSummaryFormat,
            report.ModelId,
            report.ProviderId,
            locality,
            report.TotalTokens.ToString("N0", CultureInfo.CurrentCulture),
            (report.BudgetTokens + SystemTokens(report)).ToString("N0", CultureInfo.CurrentCulture),
            report.OutputReserve.ToString("N0", CultureInfo.CurrentCulture));
        ModeNote = report.IsAssembled ? string.Empty : ChatStrings.InspectorPlainRequest;
        RedactionNotice = !report.Redacts
            ? string.Empty
            : report.RedactionCount > 0
                ? ChatStrings.Format(ChatStrings.InspectorRedactionFormat, report.RedactionCount)
                : ChatStrings.InspectorRedactionNone;
        Error = string.Empty;
        IsLoading = false;
        IsOpen = true;
    }

    /// <summary>Shows why the preview failed.</summary>
    /// <param name="message">The message.</param>
    public void ShowError(string message)
    {
        Items.Clear();
        Summary = string.Empty;
        ModeNote = string.Empty;
        RedactionNotice = string.Empty;
        Error = string.IsNullOrWhiteSpace(message) ? ChatStrings.InspectorFailed : message;
        IsLoading = false;
        IsOpen = true;
    }

    /// <summary>Closes the inspector.</summary>
    public void Close()
    {
        IsOpen = false;
        IsLoading = false;
        Items.Clear();
        Error = string.Empty;
    }

    /// <summary>Raises the commands' CanExecuteChanged (the window's Send rules changed).</summary>
    public void UpdateCommands()
    {
        SendCommand.RaiseCanExecuteChanged();
        CloseCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
    }

    /// <summary>The system prompt's tokens: the report's budget excludes it, the total includes it.</summary>
    private static int SystemTokens(ContextReportDto report) =>
        (report.Items ?? []).Where(i => i.Kind == "system").Sum(i => i.Tokens);

    private void Remove(ContextInspectorItem? item)
    {
        if (item?.Pin is { } pin)
        {
            _unpin(pin);
        }
    }
}
