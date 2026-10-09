using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Pillion.VisualStudio.Core;
using Pillion.VisualStudio.Core.Markdown;

namespace Pillion.VisualStudio.ToolWindows;

/// <summary>
/// Shows an assistant reply rendered as Markdown. The transcript virtualises and recycles its item containers, so this
/// view only hosts the reply's elements, which <see cref="MessageRenderCache"/> keeps per reply: scrolling a long
/// conversation re-attaches elements instead of rebuilding them (and their editor views).
/// </summary>
/// <remarks>This file has no Visual Studio dependency: it is also compiled into Pillion.VisualStudio.Tests.</remarks>
internal sealed class MarkdownMessageView : Decorator
{
    /// <summary>Identifies the <see cref="Turn"/> property.</summary>
    public static readonly DependencyProperty TurnProperty = DependencyProperty.Register(
        nameof(Turn),
        typeof(ChatTurnViewModel),
        typeof(MarkdownMessageView),
        new PropertyMetadata(null, (d, _) => ((MarkdownMessageView)d).Attach()));

    /// <summary>Identifies the inherited <c>Cache</c> attached property: set it once on the transcript.</summary>
    public static readonly DependencyProperty CacheProperty = DependencyProperty.RegisterAttached(
        "Cache",
        typeof(MessageRenderCache),
        typeof(MarkdownMessageView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, (d, _) => (d as MarkdownMessageView)?.Attach()));

    private RenderedMessage? _shown;

    /// <summary>Initializes a new instance of the <see cref="MarkdownMessageView"/> class.</summary>
    public MarkdownMessageView()
    {
        AutomationPropertiesHelper.SetName(this, ChatStrings.ReplyContentName);
    }

    /// <summary>Gets or sets the reply to show (<see langword="null"/> shows nothing).</summary>
    public ChatTurnViewModel? Turn
    {
        get => (ChatTurnViewModel?)GetValue(TurnProperty);
        set => SetValue(TurnProperty, value);
    }

    /// <summary>Gets the cache an element uses.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The cache.</returns>
    public static MessageRenderCache? GetCache(DependencyObject element) =>
        (MessageRenderCache?)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(CacheProperty);

    /// <summary>Sets the cache for an element and its descendants.</summary>
    /// <param name="element">The element (the transcript).</param>
    /// <param name="value">The cache.</param>
    public static void SetCache(DependencyObject element, MessageRenderCache? value) =>
        (element ?? throw new ArgumentNullException(nameof(element))).SetValue(CacheProperty, value);

    /// <summary>Called by <see cref="RenderedMessage"/> when another view takes its elements, or it is disposed.</summary>
    /// <param name="message">The message being taken away.</param>
    internal void Release(RenderedMessage message)
    {
        if (ReferenceEquals(_shown, message))
        {
            _shown = null;
            Child = null;
        }
    }

    private void Attach()
    {
        var turn = Turn;
        var cache = GetCache(this);
        var next = turn is null || cache is null ? null : cache.Get(turn);
        if (ReferenceEquals(next, _shown))
        {
            return;
        }

        _shown?.Detach(this);
        _shown = null;
        Child = null;
        if (next is not null)
        {
            next.AttachTo(this);
            _shown = next;
            Child = next.Root;
        }
    }
}

/// <summary>
/// The rendered elements of one reply, kept up to date with its text: each time the reply's <see cref="ChatTurnViewModel.Markdown"/>
/// changes (at most once per 50 ms flush while it streams) the text is parsed and only the changed tail is re-rendered.
/// </summary>
internal sealed class RenderedMessage : IDisposable
{
    private readonly ChatTurnViewModel _turn;
    private readonly MarkdownBlockRenderer _renderer;
    private readonly Stopwatch _watch = new();
    private MarkdownMessage _message = MarkdownMessage.Empty;
    private MarkdownMessageView? _host;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="RenderedMessage"/> class and renders the reply so far.</summary>
    /// <param name="turn">The reply.</param>
    /// <param name="context">The theme and services.</param>
    public RenderedMessage(ChatTurnViewModel turn, MarkdownRenderContext context)
    {
        _turn = turn ?? throw new ArgumentNullException(nameof(turn));
        Root = new StackPanel();
        _renderer = new MarkdownBlockRenderer(Root, context);
        _turn.PropertyChanged += OnTurnChanged;
        Refresh();
    }

    /// <summary>Raised after each refresh with the time it took (parse, plan and element updates), for measurement.</summary>
    public event EventHandler<RenderTimingEventArgs>? Rendered;

    /// <summary>Gets the panel holding the reply's block elements.</summary>
    public StackPanel Root { get; }

    /// <summary>Gets the reply.</summary>
    public ChatTurnViewModel Turn => _turn;

    /// <summary>Gets the renderer (for tests).</summary>
    public MarkdownBlockRenderer Renderer => _renderer;

    /// <summary>Re-renders from the reply's current text.</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        _watch.Restart();
        var next = _turn.Markdown;
        var plan = _renderer.Update(next.Blocks, !string.Equals(_message.DefinitionsKey, next.DefinitionsKey, StringComparison.Ordinal));
        _message = next;
        _watch.Stop();
        Rendered?.Invoke(this, new RenderTimingEventArgs(_watch.Elapsed, plan, next.Text.Length));
    }

    /// <summary>Shows the elements in a view, taking them from the view that showed them before.</summary>
    /// <param name="host">The view.</param>
    public void AttachTo(MarkdownMessageView host)
    {
        if (!ReferenceEquals(_host, host))
        {
            _host?.Release(this);
            _host = host;
        }
    }

    /// <summary>Forgets a view that no longer shows these elements.</summary>
    /// <param name="host">The view.</param>
    public void Detach(MarkdownMessageView host)
    {
        if (ReferenceEquals(_host, host))
        {
            _host = null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _turn.PropertyChanged -= OnTurnChanged;
        _host?.Release(this);
        _host = null;
        _renderer.Dispose();
    }

    private void OnTurnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatTurnViewModel.Markdown))
        {
            Refresh();
        }
    }
}

/// <summary>How long one refresh of a rendered reply took.</summary>
internal sealed class RenderTimingEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="RenderTimingEventArgs"/> class.</summary>
    /// <param name="elapsed">The time taken.</param>
    /// <param name="plan">The plan applied.</param>
    /// <param name="textLength">The length of the text rendered.</param>
    public RenderTimingEventArgs(TimeSpan elapsed, MarkdownRenderPlan plan, int textLength)
    {
        Elapsed = elapsed;
        Plan = plan;
        TextLength = textLength;
    }

    /// <summary>Gets the time taken.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>Gets the plan applied.</summary>
    public MarkdownRenderPlan Plan { get; }

    /// <summary>Gets the length of the text rendered.</summary>
    public int TextLength { get; }
}

/// <summary>
/// Keeps the rendered elements of the replies in the current conversation, so the virtualising transcript can recycle
/// its containers without re-rendering. The window calls <see cref="Retain"/> when turns are removed or the conversation
/// changes, which disposes the rest (closing their editor views).
/// </summary>
internal sealed class MessageRenderCache : IDisposable
{
    private readonly Dictionary<ChatTurnViewModel, RenderedMessage> _messages = [];
    private readonly MarkdownRenderContext _context;

    /// <summary>Initializes a new instance of the <see cref="MessageRenderCache"/> class.</summary>
    /// <param name="context">The theme and services every reply is rendered with.</param>
    public MessageRenderCache(MarkdownRenderContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>Raised when a reply is rendered for the first time.</summary>
    public event EventHandler<RenderedMessage>? Created;

    /// <summary>Gets how many replies are rendered.</summary>
    public int Count => _messages.Count;

    /// <summary>Gets the rendered elements of a reply, rendering it the first time.</summary>
    /// <param name="turn">The reply.</param>
    /// <returns>The rendered reply.</returns>
    public RenderedMessage Get(ChatTurnViewModel turn)
    {
        if (!_messages.TryGetValue(turn, out var message))
        {
            message = new RenderedMessage(turn, _context);
            _messages.Add(turn, message);
            Created?.Invoke(this, message);
        }

        return message;
    }

    /// <summary>Disposes every rendered reply whose turn is not in <paramref name="turns"/>.</summary>
    /// <param name="turns">The turns still shown.</param>
    public void Retain(IEnumerable<ChatTurnViewModel> turns)
    {
        var keep = new HashSet<ChatTurnViewModel>(turns);
        var drop = new List<ChatTurnViewModel>();
        foreach (var turn in _messages.Keys)
        {
            if (!keep.Contains(turn))
            {
                drop.Add(turn);
            }
        }

        foreach (var turn in drop)
        {
            _messages[turn].Dispose();
            _messages.Remove(turn);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var message in _messages.Values)
        {
            message.Dispose();
        }

        _messages.Clear();
    }
}
