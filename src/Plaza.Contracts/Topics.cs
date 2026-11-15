namespace Plaza.Contracts;

/// <summary>One topic per publishing service. Every message is keyed by order id (ADR 0008).</summary>
public static class Topics
{
    public const string Orders = "plaza.orders.v1";
    public const string Inventory = "plaza.inventory.v1";
    public const string Payments = "plaza.payments.v1";
}
