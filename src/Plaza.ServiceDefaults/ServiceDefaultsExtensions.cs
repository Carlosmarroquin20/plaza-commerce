using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Plaza.ServiceDefaults;

/// <summary>
/// Cross-cutting defaults shared by every Plaza service: telemetry, health checks and
/// service discovery for outgoing HTTP calls.
/// </summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>Name of the ActivitySource used by Plaza.Messaging.</summary>
    public const string MessagingActivitySource = "Plaza.Messaging";

    private const string LiveTag = "live";

    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        builder.AddTelemetry();

        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag]);

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Timeouts, retries of safe requests and a circuit breaker on every call (ADR 0022).
            http.AddPlazaResilience(builder.Configuration);
            http.AddServiceDiscovery();
        });

        return builder;
    }

    /// <summary>
    /// The connection string of a service's database, with the size of its connection pool
    /// capped (<c>Database:MaxPoolSize</c>, 15 by default). Every service shares one PostgreSQL
    /// server, so each gets a share of its connections: under load a service then waits for one
    /// of its own instead of taking the server's last one from everybody else (ADR 0020).
    /// </summary>
    public static string? GetPlazaConnectionString(this IConfiguration configuration, string name)
    {
        var connectionString = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString; // Design-time tools run without one.
        }

        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = configuration.GetValue("Database:MaxPoolSize", 15),
        }.ConnectionString;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Liveness: the process is up. Readiness: every registered check passes.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = c => c.Tags.Contains(LiveTag) });
        app.MapHealthChecks("/health/ready");
        return app;
    }

    private static void AddTelemetry(this IHostApplicationBuilder builder)
    {
        // HttpClient writes four log lines for every outgoing call. On a service that makes two
        // or three calls per request that is most of what it logs, and shipping it costs more
        // than it tells: the same calls are already spans in the trace (ADR 0020).
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

        // Every request is traced by default, which is what a developer wants. Under load,
        // exporting every span is real work; Telemetry:TraceSampleRatio keeps a fraction of the
        // traces, decided at the first service and followed by the rest.
        var sampleRatio = Math.Clamp(builder.Configuration.GetValue("Telemetry:TraceSampleRatio", 1.0), 0.0, 1.0);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var telemetry = builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(sampleRatio)))
                .AddSource(builder.Environment.ApplicationName)
                .AddSource(MessagingActivitySource)
                .AddAspNetCoreInstrumentation(o =>
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddNpgsql());

        // Export only when a collector is configured (Aspire sets this variable).
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }
    }
}
