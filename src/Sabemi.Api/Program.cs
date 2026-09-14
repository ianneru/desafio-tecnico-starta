using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sabemi.Api;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
var connection = builder.Configuration.GetConnectionString("Payments")
    ?? throw new InvalidOperationException("Configure ConnectionStrings__Payments.");
var apiKey = builder.Configuration["Security:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey) || Encoding.UTF8.GetByteCount(apiKey) < 32)
    throw new InvalidOperationException("Configure Security__ApiKey com pelo menos 32 bytes.");
var keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
builder.Services.AddDbContext<PaymentsDb>(options => options.UseNpgsql(connection));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
if (builder.Configuration.GetValue("Worker:Enabled", true)) builder.Services.AddHostedService<PaymentWorker>();
var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    if (context.Request.Path != "/health")
    {
        var provided = context.Request.Headers["X-Api-Key"];
        if (provided.Count != 1 || !CryptographicOperations.FixedTimeEquals(keyHash,
                SHA256.HashData(Encoding.UTF8.GetBytes(provided.ToString()))))
        {
            await Results.Problem(statusCode: 401, detail: "API key ausente ou inválida.").ExecuteAsync(context);
            return;
        }
    }
    try { await next(context); }
    catch (Exception ex) when ((ex is NpgsqlException or DbUpdateException) && !context.Response.HasStarted)
    {
        app.Logger.LogError(ex, "Falha de persistência.");
        await Results.Problem(statusCode: 503, detail: "Persistência indisponível. Tente novamente.").ExecuteAsync(context);
    }
});
app.MapPayments();
app.MapOpenApi();
app.MapGet("/health", async (PaymentsDb db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "Healthy" })
        : Results.Problem(statusCode: 503, detail: "Banco indisponível."));

if (app.Configuration.GetValue("Database:ApplyMigrations", false))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<PaymentsDb>().Database.MigrateAsync();
}
await app.RunAsync();
