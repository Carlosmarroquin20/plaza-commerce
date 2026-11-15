using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Plaza.Catalog.Data;
using Plaza.Inventory;
using Plaza.Inventory.Data;
using Plaza.Messaging.Kafka;
using Plaza.Notifications.Data;
using Plaza.Orders;
using Plaza.Orders.Data;
using Plaza.Payments;
using Plaza.Payments.Data;
using Plaza.Testing;
using Xunit;

namespace Plaza.EndToEnd.Tests;

/// <summary>
/// The five real services running in this process against a real PostgreSQL and a real
/// Redpanda. Only the HTTP transport is in-memory; messaging, persistence and the service
/// code are exactly what runs in production.
/// </summary>
public class PlazaSystem : IAsyncLifetime
{
    private readonly InfrastructureFixture _containers = new();
    private readonly List<IAsyncDisposable> _factories = [];

    /// <summary>When true, every service publishes each message twice.</summary>
    protected virtual bool DuplicateEveryMessage => false;

    public string KafkaBootstrapServers => _containers.KafkaBootstrapServers;

    public HttpClient Catalog { get; private set; } = null!;

    public HttpClient Inventory { get; private set; } = null!;

    public HttpClient Orders { get; private set; } = null!;

    public HttpClient Payments { get; private set; } = null!;

    public HttpClient Notifications { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _containers.StartAsync();

        var catalog = Create<CatalogDbContext>("catalog");
        Catalog = catalog.CreateClient();
        Inventory = Create<InventoryDbContext>("inventory").CreateClient();
        Payments = Create<PaymentsDbContext>("payments").CreateClient();
        Notifications = Create<NotificationsDbContext>("notifications").CreateClient();

        // Orders calls Catalog over HTTP: route that client to the in-memory Catalog server.
        Orders = Create<OrdersDbContext>("orders", services => services
            .AddHttpClient<CatalogClient>()
            .ConfigurePrimaryHttpMessageHandler(() => catalog.Server.CreateHandler()))
            .CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }

        await _containers.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private WebApplicationFactory<TMarker> Create<TMarker>(string service, Action<IServiceCollection>? configure = null)
        where TMarker : class
    {
        var factory = new WebApplicationFactory<TMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting($"ConnectionStrings:{service}", _containers.ConnectionString(service));
            builder.UseSetting("ConnectionStrings:kafka", _containers.KafkaBootstrapServers);
            builder.UseSetting("services:catalog:http:0", "http://localhost");
            builder.UseSetting("Seed:Enabled", "true");
            builder.UseSetting("Messaging:OutboxPollInterval", "00:00:00.020");
            builder.ConfigureTestServices(services =>
            {
                configure?.Invoke(services);
                if (DuplicateEveryMessage)
                {
                    DuplicateMessages(services);
                }
            });
        });
        _factories.Add(factory);
        return factory;
    }

    /// <summary>Wraps the service's real producer so that every batch is sent twice.</summary>
    private static void DuplicateMessages(IServiceCollection services)
    {
        var original = services.SingleOrDefault(d => d.ServiceType == typeof(IMessageProducer));
        if (original is null)
        {
            return; // Catalog does not use messaging.
        }

        services.Remove(original);
        services.AddSingleton<IMessageProducer>(provider => new DuplicatingProducer(
            (IMessageProducer)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!)));
    }

    private sealed class DuplicatingProducer(IMessageProducer inner) : IMessageProducer, IDisposable
    {
        public async Task ProduceAsync(IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken)
        {
            await inner.ProduceAsync(messages, cancellationToken);
            await inner.ProduceAsync(messages, cancellationToken);
        }

        public void Dispose() => (inner as IDisposable)?.Dispose();
    }

    // ---- Helpers shared by the tests -------------------------------------------------------

    public static readonly string[] FinalStatuses = ["Confirmed", "Rejected", "PaymentFailed"];

    public async Task SetStockAsync(Guid productId, int available)
    {
        var response = await Inventory.PutAsJsonAsync($"/stock/{productId}", new SetStockRequest(available));
        response.EnsureSuccessStatusCode();
    }

    public async Task<StockResponse> GetStockAsync(Guid productId) =>
        (await Inventory.GetFromJsonAsync<StockResponse>($"/stock/{productId}"))!;

    public Task<HttpResponseMessage> PostOrderAsync(string paymentToken, params PlaceOrderLine[] lines) =>
        Orders.PostAsJsonAsync("/orders", new PlaceOrderRequest(Guid.NewGuid(), paymentToken, lines));

    public async Task<OrderResponse> PlaceOrderAsync(string paymentToken, params PlaceOrderLine[] lines)
    {
        var response = await PostOrderAsync(paymentToken, lines);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
    }

    public Task<OrderResponse> WaitForFinalStatusAsync(Guid orderId) => Eventually.GetAsync(
        async () => (await Orders.GetFromJsonAsync<OrderResponse>($"/orders/{orderId}"))!,
        order => FinalStatuses.Contains(order.Status),
        $"order {orderId} to reach a final status");

    public async Task<List<PaymentResponse>> GetPaymentsAsync(Guid orderId) =>
        (await Payments.GetFromJsonAsync<List<PaymentResponse>>($"/payments/by-order/{orderId}"))!;

    public async Task<List<NotificationResponse>> GetNotificationsAsync(Guid orderId) =>
        (await Notifications.GetFromJsonAsync<List<NotificationResponse>>($"/notifications/by-order/{orderId}"))!;
}

/// <summary>The same system with every message delivered at least twice.</summary>
public sealed class DuplicatingPlazaSystem : PlazaSystem
{
    protected override bool DuplicateEveryMessage => true;
}

/// <summary>Products from <c>seed/products.json</c>. Each test uses its own so stock assertions are exact.</summary>
public static class SeedProducts
{
    public static readonly Guid StandingDesk = Id(1);      // 289.00
    public static readonly Guid CompactDesk = Id(2);       // 119.00
    public static readonly Guid ErgonomicChair = Id(3);    // 179.00
    public static readonly Guid TaskChair = Id(4);         //  79.00
    public static readonly Guid Monitor27 = Id(5);         // 249.00
    public static readonly Guid Monitor24 = Id(6);         // 129.00
    public static readonly Guid MonitorArm = Id(7);        //  49.00
    public static readonly Guid MechanicalKeyboard = Id(8);
    public static readonly Guid SlimKeyboard = Id(9);
    public static readonly Guid DeskLamp = Id(15);         //  32.00

    private static Guid Id(int n) => Guid.Parse($"a0000000-0000-4000-8000-{n:D12}");
}
