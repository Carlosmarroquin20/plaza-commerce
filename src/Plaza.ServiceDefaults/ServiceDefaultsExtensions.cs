using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
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
        builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());

        return builder;
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
