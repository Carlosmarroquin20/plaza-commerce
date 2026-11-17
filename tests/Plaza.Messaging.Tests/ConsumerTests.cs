using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Plaza.Testing;
using Xunit;

namespace Plaza.Messaging.Tests;

public sealed class ConsumerTests(Infrastructure infrastructure)
{
    [Fact]
    public async Task Message_delivered_twice_is_applied_once()
    {
        await using var harness = await MessagingHarness.StartAsync(infrastructure);
        var headers = new Dictionary<string, string>
        {
            [MessageHeaders.MessageId] = Guid.NewGuid().ToString(),
            [MessageHeaders.MessageType] = TestMessage.MessageType,
        };

        for (var i = 0; i < 3; i++)
        {
            await KafkaTestClient.ProduceAsync(
                harness.BootstrapServers, harness.Topic, "key", """{"value":"reserve"}""", headers);
        }

        await harness.WaitUntilDrainedAsync();
        Assert.Equal(1, await harness.CountEffectsAsync("reserve"));
        Assert.Equal(2, harness.Script.Attempts); // "reserve" once and the marker; duplicates never reach the handler
    }

    [Fact]
    public async Task Burst_is_applied_in_order_and_shares_transactions()
    {
        await using var harness = await MessagingHarness.StartAsync(infrastructure);
        var transactions = new System.Collections.Concurrent.ConcurrentBag<long>();
        harness.Script.Run = async (message, db, _) =>
        {
            db.Effects.Add(new Effect { Value = message.Value });
            if (message.Value.StartsWith("burst-", StringComparison.Ordinal))
            {
                transactions.Add(await db.Database.SqlQuery<long>($"SELECT txid_current() AS \"Value\"").SingleAsync());
            }
        };
        var values = Enumerable.Range(0, 60).Select(i => $"burst-{i:D2}").ToList();

        await harness.EnqueueManyAsync(values);

        await harness.WaitUntilDrainedAsync();
        // Every message once, in the order it was sent.
        Assert.Equal(values, await harness.EffectsInOrderAsync("burst-"));
        // And not one commit each: messages that were waiting together were applied together.
        Assert.Equal(60, transactions.Count);
        Assert.True(transactions.Distinct().Count() < 60, $"{transactions.Distinct().Count()} transactions for 60 messages");
    }

    [Fact]
    public async Task Failing_message_in_a_burst_is_parked_without_taking_its_neighbours_along()
    {
        await using var harness = await MessagingHarness.StartAsync(infrastructure, o => o.MaxAttempts = 2);
        harness.Script.Run = (message, db, _) =>
        {
            db.Effects.Add(new Effect { Value = message.Value });
            return message.Value == "mixed-poison"
                ? throw new InvalidOperationException("Simulated permanent failure.")
                : Task.CompletedTask;
        };
        var good = Enumerable.Range(0, 20).Select(i => $"mixed-{i:D2}").ToList();

        await harness.EnqueueManyAsync(good.Take(10).Append("mixed-poison").Concat(good.Skip(10)));

        var deadLetter = Assert.Single(await harness.ReadTopicUntilAsync(harness.DeadLetterTopic, m => m.Count >= 1));
        Assert.Equal("""{"value":"mixed-poison"}""", deadLetter.Value);
        Assert.Equal("2", deadLetter.Headers[MessageHeaders.DltAttempts]);

        await harness.WaitUntilDrainedAsync();
        // The batch it was in was rolled back as a whole; then each message was handled alone.
        Assert.Equal(good, await harness.EffectsInOrderAsync("mixed-"));
    }

    [Fact]
    public async Task With_batching_off_every_message_has_its_own_transaction()
    {
        await using var harness = await MessagingHarness.StartAsync(infrastructure, o => o.ConsumerBatchSize = 1);
        var transactions = new System.Collections.Concurrent.ConcurrentBag<long>();
        harness.Script.Run = async (message, db, _) =>
        {
            db.Effects.Add(new Effect { Value = message.Value });
            if (message.Value.StartsWith("single-", StringComparison.Ordinal))
            {
                transactions.Add(await db.Database.SqlQuery<long>($"SELECT txid_current() AS \"Value\"").SingleAsync());
            }
        };

        await harness.EnqueueManyAsync(Enumerable.Range(0, 20).Select(i => $"single-{i:D2}"));

        await harness.WaitUntilDrainedAsync();
        Assert.Equal(20, transactions.Distinct().Count());
    }

    [Fact]
    public async Task Transient_failure_is_retried_and_failed_attempts_leave_no_trace()
    {
        await using var harness = await MessagingHarness.StartAsync(infrastructure);
        harness.Script.Run = (message, db, attempt) =>
        {
            db.Effects.Add(new Effect { Value = message.Value });
            return message.Value == "flaky" && attempt <= 2
                ? throw new InvalidOperationException("Simulated transient failure after writing.")
                : Task.CompletedTask;
        };

        await harness.EnqueueAsync("flaky");

        await harness.WaitUntilDrainedAsync();
        // Two failed attempts wrote an effect and were rolled back; the third committed.
        Assert.Equal(1, await harness.CountEffectsAsync("flaky"));
        Assert.Equal(4, harness.Script.Attempts);
    }

    [Fact]
    public async Task Message_that_always_fails_is_dead_lettered_after_max_attempts_and_does_not_block_the_partition()
    {
        await using var harness = await MessagingHarness.StartAsync(infrastructure, o => o.MaxAttempts = 4);
        var poisonAttempts = 0;
        harness.Script.Run = (message, db, _) =>
        {
            if (message.Value == "poison")
            {
                poisonAttempts++;
                throw new InvalidOperationException("Simulated permanent failure.");
            }

            db.Effects.Add(new Effect { Value = message.Value });
            return Task.CompletedTask;
        };

        var id = await harness.EnqueueAsync("poison");

        var deadLetters = await harness.ReadTopicUntilAsync(harness.DeadLetterTopic, m => m.Count >= 1);
        var deadLetter = Assert.Single(deadLetters);
        Assert.Equal("""{"value":"poison"}""", deadLetter.Value);
        Assert.Equal(id.ToString(), deadLetter.Headers[MessageHeaders.MessageId]);
        Assert.Equal("handler-failed", deadLetter.Headers[MessageHeaders.DltReason]);
        Assert.Equal("4", deadLetter.Headers[MessageHeaders.DltAttempts]);
        Assert.Equal(harness.Topic, deadLetter.Headers[MessageHeaders.DltOriginalTopic]);
        Assert.Equal(typeof(InvalidOperationException).FullName, deadLetter.Headers[MessageHeaders.DltException]);
        Assert.Equal(4, poisonAttempts);

        await harness.WaitUntilDrainedAsync();
        Assert.Equal(0, await harness.CountEffectsAsync("poison"));
    }

    [Fact]
    public async Task Malformed_payload_is_dead_lettered_without_retries()
    {
        await using var harness = await MessagingHarness.StartAsync(infrastructure);
        var headers = new Dictionary<string, string>
        {
            [MessageHeaders.MessageId] = Guid.NewGuid().ToString(),
            [MessageHeaders.MessageType] = TestMessage.MessageType,
        };

        await KafkaTestClient.ProduceAsync(harness.BootstrapServers, harness.Topic, "key", "{not json", headers);

        var deadLetters = await harness.ReadTopicUntilAsync(harness.DeadLetterTopic, m => m.Count >= 1);
        var deadLetter = Assert.Single(deadLetters);
        Assert.Equal("deserialization-failed", deadLetter.Headers[MessageHeaders.DltReason]);
        Assert.Equal("0", deadLetter.Headers[MessageHeaders.DltAttempts]);

        await harness.WaitUntilDrainedAsync();
        Assert.Equal(1, harness.Script.Attempts); // only the marker reached the handler
    }

    [Fact]
    public async Task Message_types_without_a_handler_are_ignored()
    {
        await using var harness = await MessagingHarness.StartAsync(infrastructure);
        var headers = new Dictionary<string, string>
        {
            [MessageHeaders.MessageId] = Guid.NewGuid().ToString(),
            [MessageHeaders.MessageType] = "someone-else.event.v1",
        };

        await KafkaTestClient.ProduceAsync(harness.BootstrapServers, harness.Topic, "key", "{}", headers);

        await harness.WaitUntilDrainedAsync();
        Assert.Equal(1, harness.Script.Attempts);
    }

    [Fact]
    public async Task Handler_runs_in_the_trace_of_the_operation_that_enqueued_the_message()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("Plaza.Messaging.Tests");

        await using var harness = await MessagingHarness.StartAsync(infrastructure);
        var handlerActivity = new TaskCompletionSource<Activity?>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Script.Run = (_, _, _) =>
        {
            handlerActivity.TrySetResult(Activity.Current);
            return Task.CompletedTask;
        };

        ActivityTraceId traceId;
        ActivitySpanId spanId;
        using (var request = source.StartActivity("place order"))
        {
            Assert.NotNull(request);
            traceId = request.TraceId;
            spanId = request.SpanId;
            await harness.EnqueueAsync("traced");
        }

        var activity = await handlerActivity.Task.WaitAsync(Eventually.DefaultTimeout, TestContext.Current.CancellationToken);
        Assert.NotNull(activity);
        Assert.Equal(traceId, activity.TraceId);
        Assert.Equal(spanId, activity.ParentSpanId);
        Assert.Equal(ActivityKind.Consumer, activity.Kind);
    }
}
