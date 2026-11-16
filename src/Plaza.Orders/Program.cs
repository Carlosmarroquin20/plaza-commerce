using Microsoft.EntityFrameworkCore;
using Plaza.Contracts;
using Plaza.Messaging;
using Plaza.Orders;
using Plaza.Orders.Data;
using Plaza.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();

builder.Services.AddDbContext<OrdersDbContext>((services, options) => options
    .UseNpgsql(services.GetRequiredService<IConfiguration>().GetPlazaConnectionString("orders"))
    .UseSnakeCaseNamingConvention());
builder.Services.AddHealthChecks().AddDbContextCheck<OrdersDbContext>();

// "catalog" is resolved by service discovery (configuration key services:catalog:http:0).
builder.Services.AddHttpClient<CatalogClient>(http =>
{
    http.BaseAddress = new Uri("http://catalog");
    http.Timeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddOptions<CheckoutOptions>()
    .BindConfiguration("Checkout")
    .Validate(o => o.ReservationTtl > TimeSpan.Zero, "Checkout:ReservationTtl must be positive.")
    .Validate(o => o.ExpirySweepInterval > TimeSpan.Zero, "Checkout:ExpirySweepInterval must be positive.")
    .Validate(o => o.ExpiryBatchSize >= 1, "Checkout:ExpiryBatchSize must be at least 1.")
    .ValidateOnStart();
builder.Services.AddHostedService<OrderExpiryService>();

builder.Services.AddPlazaMessaging<OrdersDbContext>("orders")
    .Publishes(Topics.Orders)
    .Subscribe(Topics.Inventory)
    .Subscribe(Topics.Payments)
    .Handle<StockReserved, StockReservedHandler>()
    .Handle<StockRejected, StockRejectedHandler>()
    .Handle<PaymentSucceeded, PaymentSucceededHandler>()
    .Handle<PaymentFailed, PaymentFailedHandler>();

var app = builder.Build();

app.UseExceptionHandler();
app.MapDefaultEndpoints();
app.MapOrderEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Database.MigrateAsync();
}

app.Run();
