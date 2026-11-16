// Local development environment (ADR 0002): one PostgreSQL server with a database per service,
// a Redpanda broker, the five services, the simulated payment provider (ADR 0010), the shopping
// assistant, the MCP server, the public GraphQL gateway, and the Aspire dashboard collecting their
// telemetry.

var builder = DistributedApplication.CreateBuilder(args);

// A load generator is one address sending the traffic of thousands of clients. With
// --Plaza:LoadTest=true the per-caller rate limits are lifted so that it is the system that is
// measured and not its rate limiter (ADR 0020).
var loadTest = string.Equals(builder.Configuration["Plaza:LoadTest"], "true", StringComparison.OrdinalIgnoreCase);

// Images are pinned by digest; the tag is kept for readability.
var postgres = builder.AddPostgres("postgres")
    // PostgreSQL 18 with pgvector, used by the catalogue's hybrid search (ADR 0013).
    .WithImage("pgvector/pgvector", "0.8.7-pg18-trixie")
    .WithImageSHA256("ab49524d18df7200b74ac9a475e1db6297fc1eb57da4c214c63d10ecc4b754fa")
    // Seven services with a pool of 15 each, plus headroom for tools (ADR 0020).
    .WithArgs("-c", "max_connections=150");

var kafka = builder.AddRedPanda("kafka")
    .WithImage("redpandadata/redpanda", "v26.2.7")
    .WithImageSHA256("43545ee03966431f5451e0b3c1e651b39fbe3eb383360220311021f50133b649");

// Each service reads its connection string as ConnectionStrings:<service>.
var catalogDb = postgres.AddDatabase("catalog-db", databaseName: "catalog");
var inventoryDb = postgres.AddDatabase("inventory-db", databaseName: "inventory");
var ordersDb = postgres.AddDatabase("orders-db", databaseName: "orders");
var paymentsDb = postgres.AddDatabase("payments-db", databaseName: "payments");
var notificationsDb = postgres.AddDatabase("notifications-db", databaseName: "notifications");
var assistantDb = postgres.AddDatabase("assistant-db", databaseName: "assistant");
var paymentProviderDb = postgres.AddDatabase("payment-provider-db", databaseName: "payment_provider");

var catalog = builder.AddProject<Projects.Plaza_Catalog>("catalog")
    .WithReference(catalogDb, connectionName: "catalog").WaitFor(catalogDb);

var inventory = builder.AddProject<Projects.Plaza_Inventory>("inventory")
    .WithReference(inventoryDb, connectionName: "inventory").WaitFor(inventoryDb)
    .WithReference(kafka).WaitFor(kafka);

var orders = builder.AddProject<Projects.Plaza_Orders>("orders")
    .WithReference(ordersDb, connectionName: "orders").WaitFor(ordersDb)
    .WithReference(kafka).WaitFor(kafka)
    .WithReference(catalog).WaitFor(catalog);

// Stands in for an external company's API; Payments reaches it by service discovery.
var paymentProvider = builder.AddProject<Projects.Plaza_PaymentProvider>("payment-provider")
    .WithReference(paymentProviderDb, connectionName: "payment-provider").WaitFor(paymentProviderDb);

builder.AddProject<Projects.Plaza_Payments>("payments")
    .WithReference(paymentsDb, connectionName: "payments").WaitFor(paymentsDb)
    .WithReference(kafka).WaitFor(kafka)
    .WithReference(paymentProvider).WaitFor(paymentProvider);

builder.AddProject<Projects.Plaza_Notifications>("notifications")
    .WithReference(notificationsDb, connectionName: "notifications").WaitFor(notificationsDb)
    .WithReference(kafka).WaitFor(kafka);

// The assistant runs on the scripted model unless Assistant__Provider says otherwise (ADR 0012).
var assistant = builder.AddProject<Projects.Plaza_Assistant>("assistant")
    .WithReference(assistantDb, connectionName: "assistant").WaitFor(assistantDb)
    .WithReference(catalog).WaitFor(catalog)
    .WithReference(inventory).WaitFor(inventory);

builder.AddProject<Projects.Plaza_Mcp>("mcp")
    .WithReference(catalog).WaitFor(catalog)
    .WithReference(inventory).WaitFor(inventory);

// The public API (ADR 0017). Everything above is internal; clients talk to this.
var gateway = builder.AddProject<Projects.Plaza_Gateway>("gateway")
    .WithReference(catalog).WaitFor(catalog)
    .WithReference(inventory).WaitFor(inventory)
    .WithReference(orders).WaitFor(orders)
    .WithReference(assistant).WaitFor(assistant);

if (loadTest)
{
    gateway.WithEnvironment("GraphQL__RequestsPerMinute", "100000000");
    assistant.WithEnvironment("Assistant__RequestsPerMinute", "100000000");
}

builder.Build().Run();
