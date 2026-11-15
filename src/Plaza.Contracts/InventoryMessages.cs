using Plaza.Messaging;

namespace Plaza.Contracts;

// Published by Inventory on Topics.Inventory.

/// <summary>Every line of the order is reserved.</summary>
public sealed record StockReserved(Guid OrderId) : IMessage
{
    public static string MessageType => "inventory.stock-reserved.v1";
}

/// <summary>At least one line could not be reserved; nothing was reserved.</summary>
public sealed record StockRejected(Guid OrderId, string Reason) : IMessage
{
    public static string MessageType => "inventory.stock-rejected.v1";
}
