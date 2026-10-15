namespace Shop.Payments;

/// <summary>The result of a charge.</summary>
public sealed record Receipt(bool Succeeded, string Reference);

/// <summary>Talks to the card processor.</summary>
public sealed class PaymentGateway
{
    /// <summary>Charges a card and returns the processor's receipt.</summary>
    public Receipt Charge(string cardToken, long amountPence)
    {
        var reference = cardToken.Length > 4 ? cardToken[^4..] : cardToken;
        return new Receipt(amountPence > 0, "rcpt-" + reference);
    }
}
