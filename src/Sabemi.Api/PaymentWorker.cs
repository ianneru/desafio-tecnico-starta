using Microsoft.EntityFrameworkCore;

namespace Sabemi.Api;

public sealed class PaymentWorker(IServiceScopeFactory scopes, ILogger<PaymentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessOne(stoppingToken)) await Task.Delay(500, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha no worker; nova tentativa em cinco segundos.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task<bool> ProcessOne(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<PaymentsDb>();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var items = await db.Events.FromSqlRaw("""
            SELECT * FROM "PaymentEvents"
            WHERE "Canonical" = TRUE AND "ProcessingStatus" = 'Pendente' AND "NextAttemptAt" <= now()
            ORDER BY "ReceivedAt", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);

        if (items.Count == 0)
            return false;

        var item = items[0];
        await transaction.CreateSavepointAsync("processing", ct);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ContractStatuses"
                    ("ContractId", "PaymentStatus", "LastTransactionId", "Amount", "PaidAt", "EventReceivedAt", "EventId", "UpdatedAt")
                VALUES ({item.ContractId!}, {item.PaymentStatus!}, {item.TransactionId!}, {item.Amount!.Value},
                    {item.PaidAt!.Value}, {item.ReceivedAt}, {item.Id}, {DateTimeOffset.UtcNow})
                ON CONFLICT ("ContractId") DO UPDATE SET
                    "PaymentStatus" = EXCLUDED."PaymentStatus", "LastTransactionId" = EXCLUDED."LastTransactionId",
                    "Amount" = EXCLUDED."Amount", "PaidAt" = EXCLUDED."PaidAt",
                    "EventReceivedAt" = EXCLUDED."EventReceivedAt", "EventId" = EXCLUDED."EventId", "UpdatedAt" = EXCLUDED."UpdatedAt"
                WHERE (EXCLUDED."PaidAt", EXCLUDED."EventReceivedAt", EXCLUDED."EventId") >
                    ("ContractStatuses"."PaidAt", "ContractStatuses"."EventReceivedAt", "ContractStatuses"."EventId")
                """, ct);
            item.Attempts++;
            item.ProcessingStatus = "Sucesso";
            item.Error = null;
            item.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            logger.LogInformation("Evento {EventId} processado.", item.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await transaction.RollbackToSavepointAsync("processing", ct);
            db.ChangeTracker.Clear();
            item = await db.Events.SingleAsync(x => x.Id == item.Id, ct);
            item.Attempts++;
            item.Error = "Falha interna no processamento. Consulte os logs pelo ID do evento.";
            item.ProcessingStatus = item.Attempts >= 3 ? "Erro" : "Pendente";
            item.CompletedAt = item.Attempts >= 3 ? DateTimeOffset.UtcNow : null;
            item.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(5 * item.Attempts);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            logger.LogError(ex, "Falha no evento {EventId}, tentativa {Attempt}.", item.Id, item.Attempts);
        }
        return true;
    }
}