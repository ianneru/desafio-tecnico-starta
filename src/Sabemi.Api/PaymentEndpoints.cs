using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Sabemi.Api;

public static class PaymentEndpoints
{
    public static void MapPayments(this WebApplication app)
    {
        app.MapPost("/webhooks/pagamento", Receive)
            .Accepts<Payment>("application/json")
            .Produces(202)
            .Produces(200)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(409)
            .WithSummary("Recebe e persiste uma notificação de pagamento.");

        app.MapGet("/pagamentos", async (PaymentsDb db, string? status, string? id_contrato,
            int? pagina, int? tamanho, CancellationToken ct) =>
        {
            var page = pagina ?? 1;
            var size = tamanho ?? 20;

            if (page < 1 || page > 1000000 || size is < 1 or > 100 ||
                (status is not null && status is not ("Pendente" or "Sucesso" or "Erro" or "Duplicado")))
                return Results.Problem(statusCode: 400, detail: "Paginação inválida ou status desconhecido.");

            var query = db.Events.AsNoTracking();

            if (status is not null)
                query = query.Where(x => x.ProcessingStatus == status);
            if (id_contrato is not null)
                query = query.Where(x => x.ContractId == id_contrato);

            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(x => x.ReceivedAt).ThenBy(x => x.Id)
                .Skip((page - 1) * size).Take(size)
                .Select(x => new
                {
                    x.Id,
                    x.TransactionId,
                    x.ContractId,
                    x.Amount,
                    x.PaidAt,
                    x.PaymentStatus,
                    x.ProcessingStatus,
                    x.Error,
                    x.Attempts,
                    x.ReceivedAt,
                    x.CompletedAt,
                    x.OriginalEventId
                }).ToListAsync(ct);
            return Results.Ok(new { pagina = page, tamanho = size, total, items });
        }).WithSummary("Lista recebimentos, incluindo falhas e duplicidades.");

        app.MapGet("/pagamentos/{id:guid}", async (Guid id, PaymentsDb db, CancellationToken ct) =>
            await db.Events.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) is { } item
                ? Results.Ok(item) : Results.NotFound());

        app.MapGet("/contratos/{idContrato}", async (string idContrato, PaymentsDb db, CancellationToken ct) =>
            await db.Contracts.AsNoTracking().SingleOrDefaultAsync(x => x.ContractId == idContrato, ct) is { } item
                ? Results.Ok(item) : Results.NotFound());
    }

    private static async Task<IResult> Receive(HttpRequest request, PaymentsDb db, CancellationToken ct)
    {
        if (!request.HasJsonContentType())
            return Results.Problem(statusCode: 415, detail: "Utilize Content-Type: application/json.");

        using var reader = new StreamReader(request.Body, new UTF8Encoding(false, true));

        string raw;

        try
        {
            raw = await reader.ReadToEndAsync(ct);
        }
        catch (DecoderFallbackException)
        {
            return Results.Problem(statusCode: 400, detail: "Utilize UTF-8 válido.");
        }
        var (payment, error) = PaymentParser.Parse(raw);
        var item = new PaymentEvent { RawBody = raw };
        if (payment is null)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var transaction = PaymentParser.Text(doc.RootElement, "id_transacao");
                    var contract = PaymentParser.Text(doc.RootElement, "id_contrato");
                    item.TransactionId = transaction?.Length <= 100 ? transaction : null;
                    item.ContractId = contract?.Length <= 100 ? contract : null;
                }
            }
            catch (JsonException) { }

            item.ProcessingStatus = "Erro";
            item.Error = error;
            item.CompletedAt = DateTimeOffset.UtcNow;
            db.Events.Add(item);
            await db.SaveChangesAsync(ct);
            return Results.Problem(statusCode: 400, detail: error,
                extensions: new Dictionary<string, object?> { ["eventId"] = item.Id });
        }
        item.TransactionId = payment.TransactionId;
        item.ContractId = payment.ContractId;
        item.Amount = payment.Amount;
        item.PaidAt = payment.PaidAt;
        item.PaymentStatus = payment.Status;
        item.Canonical = true;
        db.Events.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/pagamentos/{item.Id}", new { eventId = item.Id, status = item.ProcessingStatus });
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_PaymentEvents_TransactionId" })
        {
            db.ChangeTracker.Clear();
            var original = await db.Events.AsNoTracking()
                .SingleAsync(x => x.Canonical && x.TransactionId == payment.TransactionId, ct);
            var equivalent = original.Matches(payment);
            item.Canonical = false;
            item.OriginalEventId = original.Id;
            item.ProcessingStatus = equivalent ? "Duplicado" : "Erro";
            item.Error = equivalent ? null : "id_transacao já recebido com dados diferentes.";
            item.CompletedAt = DateTimeOffset.UtcNow;
            db.Events.Add(item);
            await db.SaveChangesAsync(ct);
            return equivalent
                ? Results.Ok(new { eventId = original.Id, receiptId = item.Id, status = original.ProcessingStatus, duplicado = true })
                : Results.Problem(statusCode: 409, detail: item.Error,
                    extensions: new Dictionary<string, object?> { ["eventId"] = item.Id, ["originalEventId"] = original.Id });
        }
    }
}