using Plaza.Messaging;

namespace Plaza.Contracts;

// Published by Payments on Topics.Payments.

public sealed record PaymentSucceeded(Guid OrderId, Guid PaymentId) : IMessage
{
    public static string MessageType => "payments.payment-succeeded.v1";
}

public sealed record PaymentFailed(Guid OrderId, string Reason) : IMessage
{
    public static string MessageType => "payments.payment-failed.v1";
}
