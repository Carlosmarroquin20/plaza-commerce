using System.Net;
using System.Net.Sockets;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redpanda;

namespace Plaza.Testing;

/// <summary>Container images used by tests, pinned by digest.</summary>
public static class ContainerImages
{
    public const string Postgres =
        "postgres:18.7-alpine@sha256:b6972e1115a4ff8eba5d7f7d19184926d1e16d7d1136ae1e1a7ebf286da0f015";

    public const string Redpanda =
        "redpandadata/redpanda:v26.2.7@sha256:43545ee03966431f5451e0b3c1e651b39fbe3eb383360220311021f50133b649";
}

/// <summary>One PostgreSQL and, unless disabled, one Redpanda container shared by a group of tests.</summary>
/// <param name="withKafka">False for tests that only need a database.</param>
/// <param name="restartableKafka">
/// Publishes the broker on a fixed host port so that it keeps its address when it is stopped
/// and started again (<see cref="StopKafkaAsync"/>, <see cref="StartKafkaAsync"/>).
/// </param>
public sealed class InfrastructureFixture(bool withKafka = true, bool restartableKafka = false) : IAsyncDisposable
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(ContainerImages.Postgres).Build();
    private readonly RedpandaContainer? _redpanda = withKafka ? BuildRedpanda(restartableKafka) : null;

    public string KafkaBootstrapServers => Redpanda.GetBootstrapAddress();

    private RedpandaContainer Redpanda =>
        _redpanda ?? throw new InvalidOperationException("This fixture was created without Kafka.");

    public Task StartAsync() =>
        _redpanda is null ? _postgres.StartAsync() : Task.WhenAll(_postgres.StartAsync(), _redpanda.StartAsync());

    /// <summary>Stops the broker process. Topics, messages and committed offsets stay on its disk.</summary>
    public Task StopKafkaAsync() => Redpanda.StopAsync();

    public Task StartKafkaAsync() => Redpanda.StartAsync();

    /// <summary>Connection string for a database on the shared server. Tests isolate by database.</summary>
    public string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database }.ConnectionString;

    /// <summary>A database name that no other test uses.</summary>
    public static string UniqueName(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        if (_redpanda is not null)
        {
            await _redpanda.DisposeAsync();
        }
    }

    private static RedpandaContainer BuildRedpanda(bool restartable)
    {
        var builder = new RedpandaBuilder(ContainerImages.Redpanda);
        if (restartable)
        {
            // Docker hands out a new random host port on every start, and the broker advertises
            // the port it was first started with. A fixed port keeps both in agreement.
            builder = builder.WithPortBinding(FreeTcpPort(), RedpandaBuilder.RedpandaPort);
        }

        return builder.Build();
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
