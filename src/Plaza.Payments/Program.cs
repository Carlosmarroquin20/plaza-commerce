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
    .UseNpgsql(services.GetRequiredService<IConfiguration>().GetPlazaConnectionString("payments"))
    .UseSnakeCaseNamingConvention());
builder.Services.AddHealthChecks().AddDbContextCheck<PaymentsDbContext>();

// "payment-provider" is resolved by service discovery (services:payment-provider:http:0).
builder.Services.AddHttpClient<IPaymentProvider, HttpPaymentProvider>((services, http) =>
{
    http.BaseAddress = new Uri("http://payment-provider");
    // A slow provider must not hold a partition for long: give up and retry with the same key.
    http.Timeout = services.GetRequiredService<IConfiguration>()
        .GetValue("PaymentProvider:Timeout", TimeSpan.FromSeconds(5));
});
builder.Services.AddPlazaMessaging<PaymentsDbContext>("payments")
    .Publishes(Topics.Payments)
    .Subscribe(Topics.Orders)
    .Handle<PaymentRequested, PaymentRequestedHandler>()
    .Handle<OrderCancelled, OrderCancelledHandler>();

var app = builder.Build();

app.UseExceptionHandler();
app.MapDefaultEndpoints();
app.MapPaymentEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();
}

app.Run();
