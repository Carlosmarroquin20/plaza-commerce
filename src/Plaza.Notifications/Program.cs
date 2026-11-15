using Microsoft.EntityFrameworkCore;
using Plaza.Contracts;
using Plaza.Messaging;
using Plaza.Notifications;
using Plaza.Notifications.Data;
using Plaza.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();

builder.Services.AddDbContext<NotificationsDbContext>((services, options) => options
    .UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("notifications"))
    .UseSnakeCaseNamingConvention());
builder.Services.AddHealthChecks().AddDbContextCheck<NotificationsDbContext>();

builder.Services.AddPlazaMessaging<NotificationsDbContext>("notifications")
    .Subscribe(Topics.Orders)
    .Handle<OrderConfirmed, OrderConfirmedHandler>();

var app = builder.Build();

app.UseExceptionHandler();
app.MapDefaultEndpoints();

// Internal: lets support tooling and tests see what a customer was told about an order.
app.MapGet("/notifications/by-order/{orderId:guid}", async (
    Guid orderId, NotificationsDbContext db, CancellationToken cancellationToken) =>
    await db.Notifications.AsNoTracking()
        .Where(n => n.OrderId == orderId)
        .Select(n => new NotificationResponse(n.Id, n.OrderId, n.CustomerId, n.Kind, n.CreatedAt))
        .ToListAsync(cancellationToken));

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.MigrateAsync();
}

app.Run();

public sealed record NotificationResponse(Guid Id, Guid OrderId, Guid CustomerId, string Kind, DateTimeOffset CreatedAt);
