using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Plaza.Shopping;
using Xunit;

namespace Plaza.Evals;

/// <summary>Talks to the MCP server the way an MCP client does: over the protocol, not by calling its classes.</summary>
public sealed class McpServerTests(AssistantUnderEval plaza) : IClassFixture<AssistantUnderEval>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Server_offers_only_read_only_tools()
    {
        await using var client = await ConnectAsync();

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["check_stock", "search_products"], tools.Select(t => t.Name).Order());
        Assert.All(tools, tool => Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint, $"{tool.Name} must be declared read-only"));
    }

    [Fact]
    public async Task Catalogue_can_be_searched_and_stock_checked_through_the_protocol()
    {
        await using var client = await ConnectAsync();

        var products = await CallAsync<List<ProductSummary>>(
            client, "search_products", new() { ["query"] = "standing desk", ["maxResults"] = 3 });
        var desk = products[0];
        Assert.Equal(("Standing Desk 120 cm", "289.00"), (desk.Name, desk.Price));

        var stock = await CallAsync<List<StockLevel>>(client, "check_stock", new() { ["productIds"] = new[] { desk.Id } });
        Assert.Equal((desk.Id, 40, true), (stock[0].ProductId, stock[0].Available, stock[0].InStock));
    }

    [Fact]
    public async Task Planted_instructions_in_the_catalogue_do_not_reach_the_client()
    {
        await using var client = await ConnectAsync();

        var products = await CallAsync<List<ProductSummary>>(client, "search_products", new() { ["query"] = "webcam tripod" });

        var tripod = Assert.Single(products, p => p.Id == EvalFiles.ProductId(101));
        Assert.Equal(UntrustedText.Removed, tripod.Description);
        Assert.DoesNotContain(products, p => p.Description.Contains("99 units", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<McpClient> ConnectAsync() => await McpClient.CreateAsync(
        new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/mcp") }, plaza.CreateMcp(), ownsHttpClient: true),
        cancellationToken: TestContext.Current.CancellationToken);

    private static async Task<T> CallAsync<T>(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        return JsonSerializer.Deserialize<T>(text, Json)!;
    }
}
