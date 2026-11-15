using Microsoft.EntityFrameworkCore;
using Plaza.Contracts;
using Plaza.Messaging;
using Plaza.Payments;
using Plaza.Payments.Data;
using Plaza.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();

builder.Services.AddDbContext<PaymentsDbContext>((services, options) => options
    .UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("payments"))
    .UseSnakeCaseNamingConvention());
builder.Services.AddHealthChecks().AddDbContextCheck<PaymentsDbContext>();

builder.Services.AddSingleton<IPaymentProvider, SimulatedPaymentProvider>();
builder.Services.AddPlazaMessaging<PaymentsDbContext>("payments")
    .Publishes(Topics.Payments)
    .Subscribe(Topics.Orders)
    .Handle<PaymentRequested, PaymentRequestedHandler>();

var app = builder.Build();

app.UseExceptionHandler();
app.MapDefaultEndpoints();
app.MapPaymentEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();
}

app.Run();
