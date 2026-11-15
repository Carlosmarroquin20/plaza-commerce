using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plaza.PaymentProvider;
using Plaza.PaymentProvider.Data;
using Plaza.ServiceDefaults;

// Stands in for an external payment provider (ADR 0010). It is a separate process with its own
// ledger so that Plaza's Payments service meets real failure modes: timeouts, refused
// connections, and requests that were processed although the answer never arrived.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.TryAddSingleton(TimeProvider.System);

builder.Services.AddDbContext<ProviderDbContext>((services, options) => options
    .UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("payment-provider"))
    .UseSnakeCaseNamingConvention());
builder.Services.AddHealthChecks().AddDbContextCheck<ProviderDbContext>();

var app = builder.Build();

app.UseExceptionHandler();
app.MapDefaultEndpoints();
app.MapChargeEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<ProviderDbContext>().Database.MigrateAsync();
}

app.Run();
