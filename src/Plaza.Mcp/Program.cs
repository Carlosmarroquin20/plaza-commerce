using Plaza.Mcp;
using Plaza.ServiceDefaults;
using Plaza.Shopping;

// Model Context Protocol server for Plaza (ADR 0016): lets an MCP client, such as a desktop
// assistant or an agent, search the catalogue and check stock over streamable HTTP at /mcp.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();

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

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<CatalogueTools>();

var app = builder.Build();

app.UseExceptionHandler();
app.MapDefaultEndpoints();
app.MapMcp("/mcp");

app.Run();
