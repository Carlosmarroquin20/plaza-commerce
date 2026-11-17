using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Plaza.Messaging.Kafka;
using Plaza.Testing;
using Xunit;

[assembly: AssemblyFixture(typeof(Plaza.Messaging.Tests.Infrastructure))]

namespace Plaza.Messaging.Tests;

/// <summary>Containers shared by every test in the assembly.</summary>
public sealed class Infrastructure : IAsyncLifetime
{
    public InfrastructureFixture Containers { get; } = new();

    public async ValueTask InitializeAsync() => await Containers.StartAsync();

    public ValueTask DisposeAsync() => Containers.DisposeAsync();
}

public sealed record TestMessage(string Value) : IMessage
{
    public static string MessageType => "test.message.v1";
}

/// <summary>The observable effect of handling a message.</summary>
public sealed class Effect
{
    public long Id { get; private set; }

    public required string Value { get; init; }
}

public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<Effect> Effects => Set<Effect>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddMessagingTables();
}

/// <summary>What the handler does in a given test. The default records an effect.</summary>
public sealed class HandlerScript
{
    private int _attempts;

    public int Attempts => _attempts;

    public Func<TestMessage, TestDbContext, int, Task> Run { get; set; } = (message, db, _) =>
    {
        db.Effects.Add(new Effect { Value = message.Value });
        return Task.CompletedTask;
    };

    public int NextAttempt() => Interlocked.Increment(ref _attempts);
}

public sealed class ScriptedHandler(TestDbContext db, HandlerScript script) : IMessageHandler<TestMessage>
{
    public Task HandleAsync(TestMessage message, MessageContext context, CancellationToken cancellationToken) =>
        script.Run(message, db, script.NextAttempt());
}

/// <summary>
/// A host running the real outbox publisher and consumer against its own database, topic and
/// consumer group, so tests cannot observe each other's messages.
/// </summary>
public sealed class MessagingHarness : IAsyncDisposable
{
    public const string Marker = "marker";

    private readonly IHost _host;

    private MessagingHarness(IHost host, string topic, string bootstrapServers)
    {
        _host = host;
        Topic = topic;
        BootstrapServers = bootstrapServers;
    }

    public string Topic { get; }

    public string DeadLetterTopic => Topic + ".dlt";

    public string BootstrapServers { get; }

    public HandlerScript Script => _host.Services.GetRequiredService<HandlerScript>();

    public static async Task<MessagingHarness> StartAsync(
        Infrastructure infrastructure,
        Action<MessagingOptions>? configure = null,
        Func<IMessageProducer, IMessageProducer>? decorateProducer = null)
    {
        var containers = infrastructure.Containers;
        var name = InfrastructureFixture.UniqueName("messaging");
        var topic = $"{name}.events";

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:kafka"] = containers.KafkaBootstrapServers;
        builder.Services.AddDbContext<TestDbContext>(o => o.UseNpgsql(containers.ConnectionString(name)));
        builder.Services.AddSingleton<HandlerScript>();

        if (decorateProducer is not null)
        {
            builder.Services.AddSingleton<KafkaMessageProducer>();
            builder.Services.AddSingleton(sp => decorateProducer(sp.GetRequiredService<KafkaMessageProducer>()));
        }

        builder.Services
            .AddPlazaMessaging<TestDbContext>(name, options =>
            {
                options.TopicPartitions = 1;
                options.RetryBaseDelay = TimeSpan.FromMilliseconds(20);
                options.RetryMaxDelay = TimeSpan.FromMilliseconds(100);
                options.OutboxPollInterval = TimeSpan.FromMilliseconds(50);
                configure?.Invoke(options);
            })
            .Publishes(topic)
            .Subscribe(topic)
            .Handle<TestMessage, ScriptedHandler>();

        var host = builder.Build();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TestDbContext>().Database.EnsureCreatedAsync();
        }

        await host.StartAsync();
        return new MessagingHarness(host, topic, containers.KafkaBootstrapServers);
    }

    /// <summary>Runs <paramref name="action"/> with a scoped DbContext and saves.</summary>
    public async Task<T> WithDbAsync<T>(Func<TestDbContext, Task<T>> action)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TestDbContext>());
    }

    public Task<Guid> EnqueueAsync(string value) => WithDbAsync(async db =>
    {
        var id = db.EnqueueMessage(Topic, "key", new TestMessage(value));
        await db.SaveChangesAsync();
        return id;
    });

    public Task<int> CountEffectsAsync(string value) =>
        WithDbAsync(db => db.Effects.CountAsync(e => e.Value == value));

    /// <summary>
    /// Sends a marker after the messages under test and waits for it. The topic has one
    /// partition, so once the marker is handled everything before it has been dealt with.
    /// </summary>
    public async Task WaitUntilDrainedAsync()
    {
        await EnqueueAsync(Marker);
        await Eventually.UntilAsync(async () => await CountEffectsAsync(Marker) == 1, "marker message handled");
    }

    public Task<IReadOnlyList<ReceivedMessage>> ReadTopicUntilAsync(
        string topic, Func<IReadOnlyList<ReceivedMessage>, bool> until) =>
        KafkaTestClient.ReadUntilAsync(BootstrapServers, topic, until, Eventually.DefaultTimeout);

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
