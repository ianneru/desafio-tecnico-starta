using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sabemi.Api;

public sealed record Payment(
    [property: JsonPropertyName("id_transacao")] string TransactionId,
    [property: JsonPropertyName("id_contrato")] string ContractId,
    [property: JsonPropertyName("valor")] decimal Amount,
    [property: JsonPropertyName("data_pagamento")] DateTimeOffset PaidAt,
    [property: JsonPropertyName("status")] string Status);

public static class PaymentParser
{
    public static (Payment? Payment, string? Error) Parse(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, "O corpo deve ser um objeto JSON.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) return (null, "Propriedades JSON duplicadas não são permitidas.");
            var transaction = Text(root, "id_transacao");
            var contract = Text(root, "id_contrato");
            if (string.IsNullOrWhiteSpace(transaction) || transaction.Length > 100 || transaction != transaction.Trim())
                return (null, "id_transacao deve conter de 1 a 100 caracteres, sem espaços nas extremidades.");
            if (string.IsNullOrWhiteSpace(contract) || contract.Length > 100 || contract != contract.Trim())
                return (null, "id_contrato deve conter de 1 a 100 caracteres, sem espaços nas extremidades.");
            if (!root.TryGetProperty("valor", out var value) || value.ValueKind != JsonValueKind.Number ||
                !value.TryGetDecimal(out var amount) || amount <= 0 || amount > 9999999999999999.99m || decimal.Round(amount, 2) != amount)
                return (null, "valor deve ser positivo, com até 16 dígitos inteiros e 2 casas decimais.");
            var date = Text(root, "data_pagamento");
            if (date is null || !date.Contains('T') ||
                !(date.EndsWith('Z') || (date.Length >= 6 && date[^3] == ':' && (date[^6] == '+' || date[^6] == '-'))) ||
                !root.GetProperty("data_pagamento").TryGetDateTimeOffset(out var paidAt))
                return (null, "data_pagamento deve ser ISO 8601 com fuso horário.");
            var status = Text(root, "status");
            if (status is not ("Pago" or "Recusado" or "Cancelado"))
                return (null, "status deve ser Pago, Recusado ou Cancelado.");
            var utc = paidAt.ToUniversalTime();
            utc = utc.AddTicks(-(utc.Ticks % 10));
            return (new(transaction, contract, amount, utc, status), null);
        }
        catch (JsonException) { return (null, "JSON inválido."); }
    }

    public static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed class PaymentEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RawBody { get; set; } = "";
    public string? TransactionId { get; set; }
    public string? ContractId { get; set; }
    public decimal? Amount { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public string? PaymentStatus { get; set; }
    public bool Canonical { get; set; }
    public Guid? OriginalEventId { get; set; }
    public string ProcessingStatus { get; set; } = "Pendente";
    public string? Error { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public int Attempts { get; set; }

    public bool Matches(Payment payment) =>
        TransactionId == payment.TransactionId &&
        ContractId == payment.ContractId &&
        Amount == payment.Amount &&
        PaidAt == payment.PaidAt &&
        PaymentStatus == payment.Status;
}

public sealed class ContractStatus
{
    public string ContractId { get; set; } = "";
    public string PaymentStatus { get; set; } = "";
    public string LastTransactionId { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTimeOffset PaidAt { get; set; }
    public DateTimeOffset EventReceivedAt { get; set; }
    public Guid EventId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}