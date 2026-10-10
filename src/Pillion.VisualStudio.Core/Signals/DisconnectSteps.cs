using System;
using System.Collections.Generic;

namespace Pillion.VisualStudio.Core.Signals;

/// <summary>
/// The undo actions of the connections the IDE signals made (unadvise an event cookie, remove an event handler),
/// run once, in the reverse order they were added, each isolated: a step that throws is logged and the others still
/// run. Visual Studio tears its windows down before the package is disposed, so an object that was fine when the
/// connection was made (the Error List's table control is the known case) can be gone by then.
/// </summary>
public sealed class DisconnectSteps
{
    private readonly object _gate = new();
    private readonly List<(string Name, Action Undo)> _steps = [];

    /// <summary>Gets the number of steps waiting to run.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _steps.Count;
            }
        }
    }

    /// <summary>Adds the undo for a connection that has just been made.</summary>
    /// <param name="name">What the step disconnects, for the log.</param>
    /// <param name="undo">The action that disconnects it.</param>
    public void Add(string name, Action undo)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        if (undo is null)
        {
            throw new ArgumentNullException(nameof(undo));
        }

        lock (_gate)
        {
            _steps.Add((name, undo));
        }
    }

    /// <summary>Runs every step, newest first, and forgets them; a second call has nothing to do.</summary>
    /// <param name="log">The extension log, or <see langword="null"/>.</param>
    /// <returns>The number of steps that threw.</returns>
    public int RunAll(PillionLog? log)
    {
        List<(string Name, Action Undo)> steps;
        lock (_gate)
        {
            steps = [.. _steps];
            _steps.Clear();
        }

        var failed = 0;
        for (var i = steps.Count - 1; i >= 0; i--)
        {
            var (name, undo) = steps[i];
            try
            {
                undo();
            }
#pragma warning disable CA1031 // Shutting down: one step failing must not stop the others.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failed++;
                log?.Warning("signals.dispose.failed", "Disconnecting the IDE signals from " + name + " failed.", ("error", ex.GetType().Name), ("detail", ex.Message));
            }
        }

        return failed;
    }
}
