using Microsoft.EntityFrameworkCore;
using Plaza.Catalog;
using Plaza.Catalog.Data;
using Plaza.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();

// The connection string is read when the context is first resolved, so hosts that supply
// configuration late (integration tests) are honoured.
builder.Services.AddDbContext<CatalogDbContext>((services, options) => options
    .UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString("catalog"))
    .UseSnakeCaseNamingConvention());
builder.Services.AddHealthChecks().AddDbContextCheck<CatalogDbContext>();

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
}

app.Run();
