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
using Plaza.PaymentProvider;
using Plaza.PaymentProvider.Data;
using Plaza.Payments;
using Plaza.Payments.Data;
using Plaza.Testing;
using Xunit;

// Each test class starts its own PostgreSQL, Redpanda and six services. Running the classes
// one after another keeps a small machine (a laptop, a CI runner) from starving the containers:
// in parallel, PostgreSQL was seen taking longer than the 15 s connection timeout to accept.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Plaza.EndToEnd.Tests;

/// <summary>
/// The five real services and the simulated payment provider running in this process against
/// a real PostgreSQL and a real Redpanda. Only the HTTP transport is in-memory; messaging, persistence and the service
/// code are exactly what runs in production.
/// </summary>
public class PlazaSystem : IAsyncLifetime
{
    private readonly InfrastructureFixture _containers;
    private readonly List<IAsyncDisposable> _factories = [];

    public PlazaSystem()
        : this(new InfrastructureFixture())
    {
    }

    protected PlazaSystem(InfrastructureFixture containers) => _containers = containers;

    protected InfrastructureFixture Containers => _containers;

    /// <summary>Configuration applied to every service on top of the defaults.</summary>
    protected virtual IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>();

    /// <summary>Faults injected between Payments and the payment provider. None by default.</summary>
    public ProviderChaos Chaos { get; } = new();

    /// <summary>When true, every service publishes each message twice.</summary>
    protected virtual bool DuplicateEveryMessage => false;

    public string KafkaBootstrapServers => _containers.KafkaBootstrapServers;

    public HttpClient Catalog { get; private set; } = null!;

    public HttpClient Inventory { get; private set; } = null!;

    public HttpClient Orders { get; private set; } = null!;

    public HttpClient Payments { get; private set; } = null!;

    public HttpClient Notifications { get; private set; } = null!;

    /// <summary>The simulated payment provider's own API: its ledger is the truth about money.</summary>
    public HttpClient Provider { get; private set; } = null!;

    public virtual async ValueTask InitializeAsync()
    {
        await _containers.StartAsync();

        var catalog = Create<CatalogDbContext>("catalog");
        Catalog = catalog.CreateClient();
        Inventory = Create<InventoryDbContext>("inventory").CreateClient();
        var provider = Create<ProviderDbContext>("payment-provider");
        Provider = provider.CreateClient();

        // Payments calls the provider over HTTP: route that client to the in-memory provider.
        Payments = Create<PaymentsDbContext>("payments", services => services
            .AddHttpClient<IPaymentProvider, HttpPaymentProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => new ChaosHandler(Chaos, provider.Server.CreateHandler())))
            .CreateClient();
        Notifications = Create<NotificationsDbContext>("notifications").CreateClient();

        // Orders calls Catalog over HTTP: route that client to the in-memory Catalog server.
        Orders = Create<OrdersDbContext>("orders", services => services
            .AddHttpClient<CatalogClient>()
            .ConfigurePrimaryHttpMessageHandler(() => catalog.Server.CreateHandler()))
            .CreateClient();
    }

    /// <summary>
    /// Runs checkouts until one is confirmed. After start-up the consumers need a few seconds to
    /// join their groups and every service makes its first database and HTTP calls; fixtures
    /// with short timeouts call this so that those one-off delays are not mistaken for faults.
    /// </summary>
    protected async Task WarmUpAsync()
    {
        await SetStockAsync(SeedProducts.DeskLamp, 1000);
        await Eventually.UntilAsync(
            async () =>
            {
                var placed = await PlaceOrderAsync("tok_test_visa", new PlaceOrderLine(SeedProducts.DeskLamp, 1));
                return (await WaitForFinalStatusAsync(placed.Id)).Status == "Confirmed";
            },
            "the system to complete a first checkout",
            TimeSpan.FromMinutes(2));
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
            builder.UseSetting("services:payment-provider:http:0", "http://localhost");
            builder.UseSetting("Seed:Enabled", "true");
            builder.UseSetting("Messaging:OutboxPollInterval", "00:00:00.020");
            foreach (var (key, value) in Settings)
            {
                builder.UseSetting(key, value);
            }

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
            return; // Catalog and the payment provider do not use messaging.
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

    public static readonly string[] FinalStatuses = ["Confirmed", "Rejected", "PaymentFailed", "Expired"];

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

    public async Task<OrderResponse> GetOrderAsync(Guid orderId) =>
        (await Orders.GetFromJsonAsync<OrderResponse>($"/orders/{orderId}"))!;

    public Task<OrderResponse> WaitForFinalStatusAsync(Guid orderId, TimeSpan? timeout = null) => Eventually.GetAsync(
        async () => (await Orders.GetFromJsonAsync<OrderResponse>($"/orders/{orderId}"))!,
        order => FinalStatuses.Contains(order.Status),
        $"order {orderId} to reach a final status",
        timeout);

    public async Task<List<PaymentResponse>> GetPaymentsAsync(Guid orderId) =>
        (await Payments.GetFromJsonAsync<List<PaymentResponse>>($"/payments/by-order/{orderId}"))!;

    /// <summary>Every charge the provider holds for an order, whatever key it was made with.</summary>
    public async Task<List<ChargeResponse>> GetProviderChargesAsync(Guid orderId) =>
        (await Provider.GetFromJsonAsync<List<ChargeResponse>>($"/charges?reference={orderId:D}"))!;

    /// <summary>
    /// The two guarantees of the checkout, checked against the provider's ledger and the stock:
    /// a confirmed order holds exactly one charge for its total, any other order holds none,
    /// and the stock went down by exactly what was confirmed with nothing left reserved.
    /// </summary>
    public async Task AssertMoneyAndStockAreConsistentAsync(
        IReadOnlyCollection<OrderResponse> orders, Guid productId, int initialStock, int quantityPerOrder)
    {
        foreach (var order in orders)
        {
            Assert.Contains(order.Status, FinalStatuses);

            if (order.Status == "Confirmed")
            {
                var held = Assert.Single(await GetProviderChargesAsync(order.Id), c => c.Status == "Succeeded");
                Assert.Equal(order.TotalCents, held.AmountCents);
            }
            else
            {
                // A cancelled order may have been charged, but then the refund must follow.
                await Eventually.UntilAsync(
                    async () => (await GetProviderChargesAsync(order.Id)).All(c => c.Status != "Succeeded"),
                    $"no money to be held for {order.Status} order {order.Id}");
            }
        }

        var sold = orders.Count(o => o.Status == "Confirmed") * quantityPerOrder;
        await Eventually.UntilAsync(
            async () => (await GetStockAsync(productId)) is { Reserved: 0 } stock && stock.Available == initialStock - sold,
            $"stock of {productId} to be {initialStock - sold} available and 0 reserved");
    }

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
