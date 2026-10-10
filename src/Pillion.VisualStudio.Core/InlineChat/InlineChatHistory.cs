using System;
using System.Collections.Generic;
using System.Linq;

namespace Pillion.VisualStudio.Core.InlineChat;

/// <summary>
/// What inline chat remembers between prompts for as long as Visual Studio runs (nothing is written to disk): the
/// last <see cref="MaxPrompts"/> prompts, newest first, the model last picked, and the model list last fetched (so the
/// picker is filled at once the next time the prompt opens). Thread-safe.
/// </summary>
public sealed class InlineChatHistory
{
    /// <summary>The number of recent prompts kept.</summary>
    public const int MaxPrompts = 10;

    private readonly object _gate = new();
    private readonly List<string> _prompts = [];
    private IReadOnlyList<ModelOption> _models = [];
    private string? _lastModelId;

    /// <summary>Gets the recent prompts, newest first.</summary>
    public IReadOnlyList<string> Prompts
    {
        get
        {
            lock (_gate)
            {
                return _prompts.ToList();
            }
        }
    }

    /// <summary>Gets or sets the picker id of the model last picked, or <see langword="null"/> (Auto).</summary>
    public string? LastModelId
    {
        get
        {
            lock (_gate)
            {
                return _lastModelId;
            }
        }

        set
        {
            lock (_gate)
            {
                _lastModelId = value;
            }
        }
    }

    /// <summary>Gets or sets the model list last fetched (without the Auto entry).</summary>
    public IReadOnlyList<ModelOption> Models
    {
        get
        {
            lock (_gate)
            {
                return _models;
            }
        }

        set
        {
            lock (_gate)
            {
                _models = value ?? [];
            }
        }
    }

    /// <summary>Remembers a prompt: moves it to the front (once, compared without case) and keeps the newest ten.</summary>
    /// <param name="prompt">What the user sent.</param>
    public void Add(string? prompt)
    {
        var value = (prompt ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _prompts.RemoveAll(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase));
            _prompts.Insert(0, value);
            if (_prompts.Count > MaxPrompts)
            {
                _prompts.RemoveRange(MaxPrompts, _prompts.Count - MaxPrompts);
            }
        }
    }
}
