using Microsoft.EntityFrameworkCore;
using Plaza.Contracts;
using Plaza.Inventory;
using Plaza.Inventory.Data;
using Plaza.Messaging;
using Plaza.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();

builder.Services.AddDbContext<InventoryDbContext>((services, options) => options
    .UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("inventory"))
    .UseSnakeCaseNamingConvention());
builder.Services.AddHealthChecks().AddDbContextCheck<InventoryDbContext>();

builder.Services.AddScoped<StockReservationService>();
builder.Services.AddPlazaMessaging<InventoryDbContext>("inventory")
    .Publishes(Topics.Inventory)
    .Subscribe(Topics.Orders)
    .Handle<OrderPlaced, OrderPlacedHandler>()
    .Handle<OrderConfirmed, OrderConfirmedHandler>()
    .Handle<OrderCancelled, OrderCancelledHandler>();

var app = builder.Build();

app.UseExceptionHandler();
app.MapDefaultEndpoints();
app.MapStockEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
    await db.Database.MigrateAsync();
    if (app.Configuration.GetValue("Seed:Enabled", false))
    {
        await InventorySeeder.SeedAsync(db);
    }
}

app.Run();
