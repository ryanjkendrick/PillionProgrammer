using Shop.Payments;

namespace Shop.Orders;

/// <summary>Places orders and takes the payment through the payment gateway.</summary>
public sealed class OrderService
{
    private readonly PaymentGateway _gateway;

    /// <summary>Initializes a new instance of the <see cref="OrderService"/> class.</summary>
    public OrderService(PaymentGateway gateway) => _gateway = gateway;

    /// <summary>Places an order: validates it, then charges the card.</summary>
    public bool Place(Order order)
    {
        if (order.AmountPence <= 0)
        {
            return false;
        }

        var receipt = _gateway.Charge(order.CardToken, order.AmountPence);
        return receipt.Succeeded;
    }
}
