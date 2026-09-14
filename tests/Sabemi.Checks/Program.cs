using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sabemi.Api;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}");
    passed++;
}

string Payload(string id = "t1", string contract = "c1", string amount = "100.25",
    string date = "2026-09-12T12:00:00Z", string status = "Pago") =>
    $$"""{"id_transacao":"{{id}}","id_contrato":"{{contract}}","valor":{{amount}},"data_pagamento":"{{date}}","status":"{{status}}"}""";

var valid = PaymentParser.Parse(Payload());
Check(valid.Payment is not null && valid.Error is null, "payload válido");
foreach (var (body, name) in new[]
{
    ("{", "JSON malformado"), ("[]", "corpo não objeto"), ("{}", "campos ausentes"),
    (Payload(amount: "0"), "valor zero"), (Payload(amount: "-1"), "valor negativo"),
    (Payload(amount: "1.001"), "fração de centavo"), (Payload(amount: "10000000000000000"), "overflow monetário"),
    (Payload(id: " "), "ID vazio"), (Payload(id: new string('a', 101)), "ID longo"),
    (Payload(date: "2026-09-12T12:00:00"), "data sem fuso"),
    (Payload(date: "2026-02-30T12:00:00Z"), "data inexistente"),
    (Payload(status: "Outro"), "status desconhecido"),
    (Payload().Replace("\"valor\":100.25", "\"valor\":100.25,\"valor\":1"), "propriedade duplicada")
}) Check(PaymentParser.Parse(body).Error is not null, name);
Check(PaymentParser.Parse(Payload(date: "2026-09-12T09:00:00-03:00")).Payment == valid.Payment, "normalização UTC");
Check(PaymentParser.Parse(Payload(date: "2026-09-12T12:00:00.1234567Z")).Payment ==
    PaymentParser.Parse(Payload(date: "2026-09-12T12:00:00.123456Z")).Payment, "precisão de microssegundos do PostgreSQL");
Check(PaymentParser.Parse(Payload(status: "Recusado")).Payment is not null, "pagamento recusado é evento válido");
Check(PaymentParser.Parse(Payload(status: "Cancelado")).Payment is not null, "pagamento cancelado é evento válido");

if (!args.Contains("--integration"))
{
    Console.WriteLine($"{passed} verificações passaram. Integração não executada; use --integration e SABEMI_TEST_CONNECTION.");
    return;
}

var connection = Environment.GetEnvironmentVariable("SABEMI_TEST_CONNECTION")
    ?? throw new InvalidOperationException("Configure SABEMI_TEST_CONNECTION para um PostgreSQL exclusivo de testes.");
var apiDll = Environment.GetEnvironmentVariable("SABEMI_API_DLL")
    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Sabemi.Api/bin/Debug/net10.0/Sabemi.Api.dll"));
var options = new DbContextOptionsBuilder<PaymentsDb>().UseNpgsql(connection).Options;
await using (var db = new PaymentsDb(options)) await db.Database.MigrateAsync();
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();
var url = $"http://127.0.0.1:{port}";
const string key = "integration-only-key-with-at-least-32-bytes";
using var client = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
client.DefaultRequestHeaders.Add("X-Api-Key", key);
Process? server = null;
async Task Start(bool worker)
{
    var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(apiDll);
    start.Environment["ASPNETCORE_URLS"] = url;
    start.Environment["Security__ApiKey"] = key;
    start.Environment["ConnectionStrings__Payments"] = connection;
    start.Environment["Worker__Enabled"] = worker.ToString();
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
    server = Process.Start(start) ?? throw new InvalidOperationException("Falha ao iniciar API.");
    server.OutputDataReceived += (_, _) => { };
    server.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
    server.BeginOutputReadLine();
    server.BeginErrorReadLine();
    for (var i = 0; i < 100; i++)
    {
        if (server.HasExited) throw new InvalidOperationException("API terminou durante inicialização.");
        try { if ((await client.GetAsync("/health")).IsSuccessStatusCode) return; }
        catch (HttpRequestException) { }
        await Task.Delay(100);
    }
    throw new TimeoutException("API não iniciou.");
}
async Task Stop()
{
    if (server is null) return;
    if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
    server.Dispose();
    server = null;
}
Task<HttpResponseMessage> Send(string body) => client.PostAsync("/webhooks/pagamento", new StringContent(body, Encoding.UTF8, "application/json"));
async Task<JsonElement> Json(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
async Task<JsonElement> WaitFor(Guid id, string status)
{
    for (var i = 0; i < 150; i++)
    {
        var item = await Json(await client.GetAsync($"/pagamentos/{id}"));
        if (item.GetProperty("processingStatus").GetString() == status) return item;
        await Task.Delay(100);
    }
    throw new TimeoutException($"Evento {id} não atingiu {status}.");
}
var prefix = Guid.NewGuid().ToString("N");
var contractId = prefix + "-contract";
try
{
    await Start(false);
    using var anonymous = new HttpClient { BaseAddress = new Uri(url) };
    Check((await anonymous.PostAsync("/webhooks/pagamento", new StringContent(Payload()))).StatusCode == HttpStatusCode.Unauthorized, "webhook exige API key");
    Check((await anonymous.GetAsync("/pagamentos")).StatusCode == HttpStatusCode.Unauthorized, "consultas exigem API key");
    var body = Payload(prefix, contractId);
    var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Send(body)));
    Check(responses.Count(x => x.StatusCode == HttpStatusCode.Accepted) == 1 &&
        responses.Count(x => x.StatusCode == HttpStatusCode.OK) == 11, "12 requisições concorrentes: uma aceita e 11 duplicadas");
    var accepted = await Json(responses.Single(x => x.StatusCode == HttpStatusCode.Accepted));
    var id = accepted.GetProperty("eventId").GetGuid();
    var conflict = await Send(Payload(prefix, contractId, "200"));
    Check(conflict.StatusCode == HttpStatusCode.Conflict, "mesma transação com dados divergentes");
    var invalid = await Send(Payload(prefix + "-bad", contractId, "-1"));
    Check(invalid.StatusCode == HttpStatusCode.BadRequest, "validação retorna 400");
    var errors = await Json(await client.GetAsync($"/pagamentos?status=Erro&id_contrato={contractId}"));
    Check(errors.GetProperty("total").GetInt32() == 2, "filtro de erros inclui conflito e validação");
    Check((await client.GetAsync("/pagamentos?pagina=0")).StatusCode == HttpStatusCode.BadRequest, "paginação inválida");
    await Stop();
    await Start(true);
    var completed = await WaitFor(id, "Sucesso");
    Check(completed.GetProperty("attempts").GetInt32() == 1, "recupera evento persistido após reinício e processa uma vez");
    var clock = Stopwatch.StartNew();
    var next = await Send(Payload(prefix + "-new", contractId, "50", "2026-09-13T12:00:00Z"));
    clock.Stop();
    Check(next.StatusCode == HttpStatusCode.Accepted && clock.Elapsed < TimeSpan.FromSeconds(2), "webhook não espera os dois segundos do worker");
    var nextId = (await Json(next)).GetProperty("eventId").GetGuid();
    await WaitFor(nextId, "Sucesso");
    var old = await Send(Payload(prefix + "-old", contractId, "10", "2026-09-11T12:00:00Z"));
    await WaitFor((await Json(old)).GetProperty("eventId").GetGuid(), "Sucesso");
    var contract = await Json(await client.GetAsync($"/contratos/{contractId}"));
    Check(contract.GetProperty("lastTransactionId").GetString() == prefix + "-new", "evento antigo não regride contrato");
    Check((await Send(body)).StatusCode == HttpStatusCode.OK, "reenvio após conclusão não reprocessa");
    await using var verify = new PaymentsDb(options);
    Check(await verify.Events.CountAsync(x => x.TransactionId == prefix && x.Canonical) == 1, "unicidade persistida no PostgreSQL");
    Check((await verify.Events.SingleAsync(x => x.Id == id)).Attempts == 1, "efeito idempotente confirmado no banco");
    Check((await client.GetAsync("/openapi/v1.json")).IsSuccessStatusCode, "documentação OpenAPI disponível");
}
finally { await Stop(); }
Console.WriteLine($"{passed} verificações passaram, incluindo PostgreSQL e HTTP reais.");
