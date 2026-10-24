namespace Shop.Orders;

/// <summary>An order placed by a customer.</summary>
public sealed class Order
{
    /// <summary>Gets the order number.</summary>
    public int Number { get; init; }

    /// <summary>Gets the amount to charge, in pence.</summary>
    public long AmountPence { get; init; }

    /// <summary>Gets the customer's card token.</summary>
    public string CardToken { get; init; } = string.Empty;
}
