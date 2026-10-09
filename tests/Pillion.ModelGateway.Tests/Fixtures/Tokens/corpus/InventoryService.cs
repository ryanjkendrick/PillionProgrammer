using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Contoso.Warehouse.Inventory;

/// <summary>A stock keeping unit and the quantity on hand in one bin.</summary>
/// <param name="Sku">The stock keeping unit, for example <c>WID-0042</c>.</param>
/// <param name="Bin">The bin code, aisle then shelf.</param>
/// <param name="Quantity">The number of items in the bin.</param>
public sealed record StockLevel(string Sku, string Bin, int Quantity);

/// <summary>Thrown when a reservation asks for more than the warehouse holds.</summary>
public sealed class InsufficientStockException : Exception
{
    public InsufficientStockException(string sku, int requested, int available)
        : base($"Cannot reserve {requested} of {sku}: only {available} available.")
    {
        Sku = sku;
        Requested = requested;
        Available = available;
    }

    public string Sku { get; }

    public int Requested { get; }

    public int Available { get; }
}

/// <summary>Keeps stock levels per bin and hands out reservations for pick lists.</summary>
public sealed partial class InventoryService : IDisposable
{
    private readonly ConcurrentDictionary<string, List<StockLevel>> _levels = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<InventoryService> _logger;
    private readonly TimeProvider _time;
    private int _reservationCount;

    public InventoryService(ILogger<InventoryService> logger, TimeProvider? time = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = time ?? TimeProvider.System;
    }

    public int ReservationCount => Volatile.Read(ref _reservationCount);

    public void Receive(string sku, string bin, int quantity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        ArgumentException.ThrowIfNullOrWhiteSpace(bin);
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Received quantity must be positive.");
        }

        var bins = _levels.GetOrAdd(sku, _ => new List<StockLevel>());
        lock (bins)
        {
            var index = bins.FindIndex(level => string.Equals(level.Bin, bin, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                bins[index] = bins[index] with { Quantity = bins[index].Quantity + quantity };
            }
            else
            {
                bins.Add(new StockLevel(sku, bin, quantity));
            }
        }

        LogReceived(_logger, sku, bin, quantity);
    }

    public int OnHand(string sku)
    {
        if (!_levels.TryGetValue(sku, out var bins))
        {
            return 0;
        }

        lock (bins)
        {
            return bins.Sum(level => level.Quantity);
        }
    }

    public async Task<IReadOnlyList<StockLevel>> ReserveAsync(string sku, int quantity, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var available = OnHand(sku);
            if (available < quantity)
            {
                throw new InsufficientStockException(sku, quantity, available);
            }

            var picks = new List<StockLevel>();
            var bins = _levels[sku];
            lock (bins)
            {
                // Pick from the fullest bins first so small remainders are consolidated later.
                foreach (var level in bins.OrderByDescending(l => l.Quantity).ToList())
                {
                    if (quantity == 0)
                    {
                        break;
                    }

                    var take = Math.Min(level.Quantity, quantity);
                    picks.Add(level with { Quantity = take });
                    quantity -= take;
                    var index = bins.IndexOf(level);
                    bins[index] = level with { Quantity = level.Quantity - take };
                }

                bins.RemoveAll(l => l.Quantity == 0);
            }

            Interlocked.Increment(ref _reservationCount);
            return picks;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string Describe(string sku)
    {
        var onHand = OnHand(sku);
        var stamp = _time.GetUtcNow().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return onHand switch
        {
            0 => $"{sku}: out of stock ({stamp})",
            < 10 => $"{sku}: low, {onHand} left ({stamp})",
            _ => $"{sku}: {onHand} on hand ({stamp})",
        };
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Received {Quantity} of {Sku} into {Bin}")]
    private static partial void LogReceived(ILogger logger, string sku, string bin, int quantity);
}
