using Plaza.Messaging.Kafka;
using Xunit;

namespace Plaza.Messaging.Tests;

public sealed class OutboxTests(Infrastructure infrastructure)
{
    [Fact]
    public async Task Committed_message_is_published_with_metadata_headers()
    {
        await using var harness = await MessagingHarness.StartAsync(infrastructure);

        var id = await harness.EnqueueAsync("hello");

        var messages = await harness.ReadTopicUntilAsync(harness.Topic, m => m.Count >= 1);
        var message = Assert.Single(messages);
        Assert.Equal("key", message.Key);
        Assert.Equal("""{"value":"hello"}""", message.Value);
        Assert.Equal(id.ToString(), message.Headers[MessageHeaders.MessageId]);
        Assert.Equal(TestMessage.MessageType, message.Headers[MessageHeaders.MessageType]);
        Assert.True(DateTimeOffset.TryParse(message.Headers[MessageHeaders.OccurredAt], out _));
    }

    [Fact]
    public async Task Rolled_back_transaction_publishes_nothing()
    {
        await using var harness = await MessagingHarness.StartAsync(infrastructure);

        await harness.WithDbAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            db.Effects.Add(new Effect { Value = "business change" });
            db.EnqueueMessage(harness.Topic, "key", new TestMessage("rolled back"));
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
            return 0;
        });
        await harness.EnqueueAsync(MessagingHarness.Marker);

        var messages = await harness.ReadTopicUntilAsync(
            harness.Topic, m => m.Any(x => x.Value.Contains(MessagingHarness.Marker)));

        // The marker was enqueued later; if the rolled-back message existed it would precede it.
        Assert.Single(messages);
        Assert.Equal(0, await harness.CountEffectsAsync("business change"));
    }

    [Fact]
    public async Task Messages_are_published_in_the_order_they_were_enqueued()
    {
        await using var harness = await MessagingHarness.StartAsync(infrastructure, o => o.OutboxBatchSize = 7);

        var expected = Enumerable.Range(0, 50).Select(i => $"m{i}").ToList();
        await harness.WithDbAsync(async db =>
        {
            foreach (var value in expected)
            {
                db.EnqueueMessage(harness.Topic, "key", new TestMessage(value));
            }

            return await db.SaveChangesAsync();
        });

        var messages = await harness.ReadTopicUntilAsync(harness.Topic, m => m.Count >= expected.Count);

        Assert.Equal(expected.Select(v => $$"""{"value":"{{v}}"}"""), messages.Select(m => m.Value));
    }

    [Fact]
    public async Task Publisher_crash_after_broker_ack_republishes_and_consumer_applies_once()
    {
        // The broker acknowledges the batch, then the publisher fails before marking the rows
        // as published: the classic outbox window. The batch must go out again (nothing lost)
        // and the consumer must apply it once (nothing duplicated).
        var crashing = new CrashOnceAfterProduce();
        await using var harness = await MessagingHarness.StartAsync(
            infrastructure, decorateProducer: inner => crashing.Wrap(inner));

        var id = await harness.EnqueueAsync("charge");

        var messages = await harness.ReadTopicUntilAsync(harness.Topic, m => m.Count >= 2);
        Assert.All(messages, m => Assert.Equal(id.ToString(), m.Headers[MessageHeaders.MessageId]));
        Assert.Equal(1, crashing.Crashes);

        await harness.WaitUntilDrainedAsync();
        Assert.Equal(1, await harness.CountEffectsAsync("charge"));
    }

    private sealed class CrashOnceAfterProduce
    {
        private int _crashes;

        public int Crashes => _crashes;

        public IMessageProducer Wrap(IMessageProducer inner) => new Producer(inner, this);

        private sealed class Producer(IMessageProducer inner, CrashOnceAfterProduce owner) : IMessageProducer
        {
            public async Task ProduceAsync(IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken)
            {
                await inner.ProduceAsync(messages, cancellationToken);
                if (Interlocked.CompareExchange(ref owner._crashes, 1, 0) == 0)
                {
                    throw new InvalidOperationException("Simulated crash after the broker acknowledged the batch.");
                }
            }
        }
    }
}
