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

/// <summary>One PostgreSQL and one Redpanda container shared by a test assembly.</summary>
public sealed class InfrastructureFixture : IAsyncDisposable
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(ContainerImages.Postgres).Build();
    private readonly RedpandaContainer _redpanda = new RedpandaBuilder(ContainerImages.Redpanda).Build();

    public string KafkaBootstrapServers => _redpanda.GetBootstrapAddress();

    public Task StartAsync() => Task.WhenAll(_postgres.StartAsync(), _redpanda.StartAsync());

    /// <summary>Connection string for a database on the shared server. Tests isolate by database.</summary>
    public string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database }.ConnectionString;

    /// <summary>A database name that no other test uses.</summary>
    public static string UniqueName(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _redpanda.DisposeAsync();
    }
}
