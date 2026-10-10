using System;
using System.Collections.Generic;

namespace Pillion.VisualStudio.Core.Signals;

/// <summary>A bounded record of the latest signals, oldest dropped first. Thread-safe.</summary>
public sealed class SignalRingBuffer
{
    /// <summary>The number of events kept.</summary>
    public const int DefaultCapacity = 200;

    private readonly object _gate = new();
    private readonly IdeSignalEvent[] _items;
    private int _start;
    private int _count;

    /// <summary>Initializes a new instance of the <see cref="SignalRingBuffer"/> class.</summary>
    /// <param name="capacity">How many events to keep; defaults to <see cref="DefaultCapacity"/>.</param>
    public SignalRingBuffer(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _items = new IdeSignalEvent[capacity];
    }

    /// <summary>Gets how many events are kept now.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Adds an event, dropping the oldest when the buffer is full.</summary>
    /// <param name="item">The event.</param>
    public void Add(IdeSignalEvent item)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        lock (_gate)
        {
            if (_count < _items.Length)
            {
                _items[(_start + _count) % _items.Length] = item;
                _count++;
            }
            else
            {
                _items[_start] = item;
                _start = (_start + 1) % _items.Length;
            }
        }
    }

    /// <summary>Copies the events, oldest first.</summary>
    /// <returns>The events.</returns>
    public IReadOnlyList<IdeSignalEvent> Snapshot()
    {
        lock (_gate)
        {
            var copy = new IdeSignalEvent[_count];
            for (var i = 0; i < _count; i++)
            {
                copy[i] = _items[(_start + i) % _items.Length];
            }

            return copy;
        }
    }
}
