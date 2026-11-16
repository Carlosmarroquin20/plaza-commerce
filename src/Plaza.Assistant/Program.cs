using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Plaza.Assistant;
using Plaza.Assistant.Data;
using Plaza.Assistant.Models;
using Plaza.ServiceDefaults;
using Plaza.Shopping;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.TryAddSingleton(TimeProvider.System);

builder.Services.AddOptions<AssistantOptions>()
    .BindConfiguration("Assistant")
    .Validate(o => o.MaxCostPerConversationUsd > 0, "Assistant:MaxCostPerConversationUsd must be positive.")
    .Validate(o => o.MaxTurnsPerConversation >= 1, "Assistant:MaxTurnsPerConversation must be at least 1.")
    .Validate(o => o.MaxInputCharacters is >= 1 and <= 20_000, "Assistant:MaxInputCharacters must be between 1 and 20000.")
    .Validate(o => o.MaxOutputTokens >= 1, "Assistant:MaxOutputTokens must be at least 1.")
    .Validate(o => o.MaxToolRounds is >= 1 and <= 20, "Assistant:MaxToolRounds must be between 1 and 20.")
    .Validate(o => o.InputUsdPerMillionTokens >= 0 && o.OutputUsdPerMillionTokens >= 0, "Token prices cannot be negative.")
    .Validate(o => o.RequestsPerMinute >= 1, "Assistant:RequestsPerMinute must be at least 1.")
    .ValidateOnStart();

builder.Services.AddDbContext<AssistantDbContext>((services, options) => options
    .UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("assistant"))
    .UseSnakeCaseNamingConvention());
builder.Services.AddHealthChecks().AddDbContextCheck<AssistantDbContext>();

// "catalog" and "inventory" are resolved by service discovery.
builder.Services.AddHttpClient<CatalogApi>(http =>
{
    http.BaseAddress = new Uri("http://catalog");
    http.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient<InventoryApi>(http =>
{
    http.BaseAddress = new Uri("http://inventory");
    http.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddScoped<ShoppingService>();

builder.Services.AddSingleton<SystemPrompt>();
builder.Services.AddSingleton(services =>
    ChatClientFactory.Create(services.GetRequiredService<IOptions<AssistantOptions>>().Value, services));
builder.Services.AddScoped<ConversationService>();

// Every message can cost money, so the number a single client may send is bounded.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy(AssistantEndpoints.RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = context.RequestServices.GetRequiredService<IOptions<AssistantOptions>>().Value.RequestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
        }));
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseRateLimiter();
app.MapDefaultEndpoints();
app.MapAssistantEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<AssistantDbContext>().Database.MigrateAsync();
}

app.Run();
