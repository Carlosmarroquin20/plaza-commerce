using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Plaza.Gateway;
using Plaza.Gateway.Clients;
using Plaza.Gateway.GraphQL;
using Plaza.ServiceDefaults;

// The public API of Plaza (ADR 0017): one GraphQL endpoint for clients, in front of the
// internal REST APIs. It is the only service meant to be reachable from outside.

var builder = WebApplication.CreateBuilder(args);
var development = builder.Environment.IsDevelopment();

// A GraphQL request is a query and a few variables; nothing legitimate is larger than this.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 256 * 1024);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.TryAddSingleton(TimeProvider.System);

var localSigningKey = builder.AddGatewayAuthentication();

// Internal services, resolved by service discovery.
void AddInternalClient<TClient>(string service)
    where TClient : class =>
    builder.Services.AddHttpClient<TClient>(http =>
    {
        http.BaseAddress = new Uri($"http://{service}");
        http.Timeout = TimeSpan.FromSeconds(10);
    });

AddInternalClient<CatalogClient>("catalog");
AddInternalClient<InventoryClient>("inventory");
AddInternalClient<OrdersClient>("orders");
AddInternalClient<AssistantClient>("assistant");

var graphql = builder.Configuration.GetSection("GraphQL");
builder.Services
    .AddGraphQLServer()
    .AddAuthorization()
    .AddQueryType<Query>()
    .AddMutationType<Mutation>()
    .AddTypeExtension<ProductResolvers>()
    .AddTypeExtension<OrderItemResolvers>()
    .AddDataLoader<StockByProductIdDataLoader>()
    .AddDataLoader<ProductByIdDataLoader>()
    // A client chooses the shape of its query, so the server bounds how deep and how
    // expensive a query may be before running any of it (ADR 0019).
    .AddMaxExecutionDepthRule(graphql.GetValue("MaxDepth", 6), skipIntrospectionFields: true)
    .ModifyCostOptions(cost =>
    {
        cost.MaxFieldCost = graphql.GetValue("MaxFieldCost", 1000);
        cost.MaxTypeCost = graphql.GetValue("MaxTypeCost", 1000);
        cost.EnforceCostLimits = true;
    })
    .ModifyRequestOptions(request =>
    {
        request.ExecutionTimeout = graphql.GetValue("ExecutionTimeout", TimeSpan.FromSeconds(15));
        // Stack traces and exception messages of internal failures stay inside.
        request.IncludeExceptionDetails = development;
    })
    // The schema is published in the repository; production does not answer questions about it.
    .DisableIntrospection(!development);

// Per caller when there is a token, per address otherwise.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy("per-caller", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue("sub") ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = graphql.GetValue("RequestsPerMinute", 120),
            Window = TimeSpan.FromMinutes(1),
        }));
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();

// A request without a token is anonymous and may read the catalogue. A request that presents
// a token that does not validate (expired, forged, malformed) is refused outright, so a client
// finds out that its token is bad instead of being quietly treated as anonymous.
app.Use(async (context, next) =>
{
    if (context.Request.Headers.ContainsKey("Authorization") && context.User.Identity?.IsAuthenticated != true)
    {
        await context.ChallengeAsync();
        return;
    }

    await next(context);
});

app.UseRateLimiter();
app.UseAuthorization();
app.MapDefaultEndpoints();

app.MapGraphQL("/graphql")
    .WithOptions(options =>
    {
        // The in-browser IDE is a development tool.
        options.Tool.Enable = development;
        // Neither is the schema download; it is published in the repository instead.
        options.EnableSchemaRequests = development;
        // Queries travel in the body: a query string ends up in logs and caches.
        options.EnableGetRequests = false;
    })
    .RequireRateLimiting("per-caller");

if (development && localSigningKey is not null)
{
    // Development only: hands out a token for a new customer so the API can be tried without
    // an identity provider. It does not exist in any other environment.
    app.MapPost("/dev/token", (TimeProvider timeProvider) =>
    {
        var customerId = Guid.NewGuid();
        var lifetime = TimeSpan.FromHours(1);
        return Results.Ok(new
        {
            customerId,
            accessToken = GatewayAuth.CreateLocalToken(localSigningKey, customerId, lifetime, timeProvider),
            expiresInSeconds = (int)lifetime.TotalSeconds,
        });
    });
}

app.Run();
