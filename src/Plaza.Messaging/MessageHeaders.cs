using System.Diagnostics;
using System.Text.Json;

namespace Plaza.Messaging;

/// <summary>Names of the Kafka headers that carry message metadata (ADR 0008).</summary>
public static class MessageHeaders
{
    public const string MessageId = "message-id";
    public const string MessageType = "message-type";
    public const string OccurredAt = "occurred-at";
    public const string TraceParent = "traceparent";

    public const string DltReason = "dlt-reason";
    public const string DltException = "dlt-exception";
    public const string DltAttempts = "dlt-attempts";
    public const string DltOriginalTopic = "dlt-original-topic";
    public const string DltOriginalPartition = "dlt-original-partition";
    public const string DltOriginalOffset = "dlt-original-offset";
}

internal static class MessagingDefaults
{
    public const string ActivitySourceName = "Plaza.Messaging";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string DeadLetterTopic(string topic) => topic + ".dlt";
}
