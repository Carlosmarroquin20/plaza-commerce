using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Plaza.Catalog;
using Plaza.Catalog.Data;
using Plaza.Catalog.Search;
using Plaza.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();

// The connection string is read when the context is first resolved, so hosts that supply
// configuration late (integration tests) are honoured.
builder.Services.AddDbContext<CatalogDbContext>((services, options) => options
    .UseNpgsql(services.GetRequiredService<IConfiguration>().GetPlazaConnectionString("catalog"), npgsql => npgsql.UseVector())
    .UseSnakeCaseNamingConvention());
builder.Services.AddHealthChecks().AddDbContextCheck<CatalogDbContext>();

// The embedding model is swappable behind this interface; the default needs no model at all.
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, HashingEmbeddingGenerator>();
builder.Services.AddScoped<ProductSearch>();

// In-memory today; the same API takes a distributed cache when Catalog runs as several instances.
builder.Services.AddHybridCache();

var app = builder.Build();

app.UseExceptionHandler();
app.MapDefaultEndpoints();
app.MapProductEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    await db.Database.MigrateAsync();
    if (app.Configuration.GetValue("Seed:Enabled", false))
    {
        await CatalogSeeder.SeedAsync(db);
    }

    await ProductIndexer.IndexMissingAsync(
        db, scope.ServiceProvider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
}

app.Run();
