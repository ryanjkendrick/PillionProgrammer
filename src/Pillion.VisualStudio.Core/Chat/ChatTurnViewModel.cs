using System.Windows.Input;
using Pillion.VisualStudio.Core.Markdown;

namespace Pillion.VisualStudio.Core;

/// <summary>Who wrote a turn of the transcript.</summary>
public enum ChatTurnRole
{
    /// <summary>The developer.</summary>
    User,

    /// <summary>The model.</summary>
    Assistant,
}

/// <summary>Where a turn of the transcript is in its life.</summary>
public enum ChatTurnState
{
    /// <summary>The turn is complete (always the case for user turns).</summary>
    Complete,

    /// <summary>The reply is still streaming.</summary>
    Streaming,

    /// <summary>The developer pressed Stop; the text so far is kept.</summary>
    Stopped,

    /// <summary>The host or the model reported an error.</summary>
    Failed,

    /// <summary>Pillion Host restarted or disconnected while the reply was streaming.</summary>
    Interrupted,
}

/// <summary>One turn of the chat transcript: a user message or an assistant reply.</summary>
public sealed class ChatTurnViewModel : ObservableObject
{
    private string _text = string.Empty;
    private MarkdownMessage? _markdown;
    private ChatTurnState _state;
    private string? _errorMessage;
    private string? _errorHint;
    private bool _canRetry;

    private string _header;

    private ChatTurnViewModel(ChatTurnRole role, string header, string? modelId, ChatTurnState state)
    {
        Role = role;
        _header = header;
        ModelId = modelId;
        _state = state;
    }

    /// <summary>Gets who wrote the turn.</summary>
    public ChatTurnRole Role { get; }

    /// <summary>Gets a value indicating whether the developer wrote the turn.</summary>
    public bool IsUser => Role == ChatTurnRole.User;

    /// <summary>
    /// Gets the turn's header: "You", or the model and provider that answered. For a reply it starts as the picked
    /// model and changes to the model the host names in the streamed chunks.
    /// </summary>
    public string Header
    {
        get => _header;
        private set
        {
            if (SetProperty(ref _header, value))
            {
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    /// <summary>Gets the picker id of the model the reply was requested from (Auto included), for Retry.</summary>
    public string? ModelId { get; }

    /// <summary>Gets the text so far.</summary>
    public string Text
    {
        get => _text;
        private set
        {
            if (SetProperty(ref _text, value))
            {
                _markdown = null;
                OnPropertyChanged(nameof(Markdown));
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    /// <summary>
    /// Gets the text parsed as Markdown, for display. Parsed when first read after the text changed, so a streaming reply
    /// is parsed at most once per batch of chunks the view model pushes (every 50 ms), and only while a view shows it.
    /// </summary>
    public MarkdownMessage Markdown => _markdown ??= ChatMarkdownParser.Parse(_text);

    /// <summary>Gets the turn's state.</summary>
    public ChatTurnState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsStreaming));
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(HasStateText));
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    /// <summary>Gets a value indicating whether the reply is still streaming.</summary>
    public bool IsStreaming => State == ChatTurnState.Streaming;

    /// <summary>Gets a short note about the turn's state ("Stopped.", "Interrupted: ..."), or an empty string.</summary>
    public string StateText => State switch
    {
        ChatTurnState.Streaming => "Answering...",
        ChatTurnState.Stopped => "Stopped.",
        ChatTurnState.Interrupted => "Interrupted: Pillion Host restarted before the reply finished.",
        _ => string.Empty,
    };

    /// <summary>Gets a value indicating whether <see cref="StateText"/> is not empty.</summary>
    public bool HasStateText => StateText.Length > 0;

    /// <summary>Gets the user-facing error text, when <see cref="State"/> is <see cref="ChatTurnState.Failed"/>.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>Gets the error's retry hint, when the host supplied one.</summary>
    public string? ErrorHint
    {
        get => _errorHint;
        private set
        {
            if (SetProperty(ref _errorHint, value))
            {
                OnPropertyChanged(nameof(HasErrorHint));
            }
        }
    }

    /// <summary>Gets a value indicating whether an error message is shown.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Gets a value indicating whether a retry hint is shown.</summary>
    public bool HasErrorHint => !string.IsNullOrEmpty(ErrorHint);

    /// <summary>Gets a value indicating whether the Retry action is offered for this turn.</summary>
    public bool CanRetry
    {
        get => _canRetry;
        internal set => SetProperty(ref _canRetry, value);
    }

    /// <summary>Gets the command that retries this reply (the conversation's <see cref="ChatViewModel.RetryCommand"/>).</summary>
    public ICommand? RetryCommand { get; internal set; }

    /// <summary>Gets the name a screen reader announces for the turn.</summary>
    public string AutomationName => IsUser
        ? "You said: " + Text
        : "Reply from " + Header + (HasStateText ? ". " + StateText : string.Empty);

    /// <summary>Gets or sets the request id of the reply, for assistant turns.</summary>
    internal string? RequestId { get; set; }

    /// <summary>Creates a user turn.</summary>
    /// <param name="text">The message.</param>
    /// <returns>The turn.</returns>
    public static ChatTurnViewModel ForUser(string text)
    {
        var turn = new ChatTurnViewModel(ChatTurnRole.User, "You", modelId: null, ChatTurnState.Complete);
        turn.Text = text ?? string.Empty;
        return turn;
    }

    /// <summary>Creates a streaming assistant turn whose header names the model and provider.</summary>
    /// <param name="model">The model that is answering.</param>
    /// <returns>The turn.</returns>
    public static ChatTurnViewModel ForAssistant(ModelOption model)
    {
        if (model is null)
        {
            throw new System.ArgumentNullException(nameof(model));
        }

        return new ChatTurnViewModel(ChatTurnRole.Assistant, model.ReplyHeader, model.Id, ChatTurnState.Streaming);
    }

    /// <summary>Names the model that is answering. Call on the UI thread.</summary>
    /// <param name="header">The reply header, for example "gemma4:e2b via Ollama, local".</param>
    internal void SetHeader(string header)
    {
        if (!string.IsNullOrWhiteSpace(header))
        {
            Header = header;
        }
    }

    /// <summary>Appends streamed text. Call on the UI thread.</summary>
    /// <param name="text">The text to append.</param>
    internal void Append(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Text += text;
        }
    }

    /// <summary>Ends the reply. Call on the UI thread.</summary>
    /// <param name="state">The final state.</param>
    /// <param name="errorMessage">The error text for <see cref="ChatTurnState.Failed"/>.</param>
    /// <param name="errorHint">The retry hint, if any.</param>
    internal void Finish(ChatTurnState state, string? errorMessage = null, string? errorHint = null)
    {
        ErrorMessage = errorMessage;
        ErrorHint = errorHint;
        State = state;
    }
}
