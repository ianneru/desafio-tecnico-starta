# SABEMI — API de notificações de pagamento

Recebe webhooks de pagamento autenticados, guarda o log bruto de cada tentativa, impede processamento duplicado e processa em background. Inclui painel web de acompanhamento.

**Stack:** ASP.NET Core (.NET 10) + EF Core · PostgreSQL 17 · Next.js 16 · Docker Compose

## Subir

```bash
git clone https://github.com/ianneru/desafio-tecnico-starta.git
cd desafio-tecnico-starta
# crie o .env na raiz com as credenciais (veja abaixo)
docker compose up --build -d
```

- API: `http://localhost:5070` · Painel: `http://localhost:3000` · Banco: `localhost:5432`
- Os dados ficam no volume `postgres-data`; evite `docker compose down -v` para não apagá-los.

## Credenciais e login

O repositório **não** traz senha nem API key. Crie o `.env` na raiz (está no `.gitignore`, nunca versione):

```ini
POSTGRES_PASSWORD=defina-uma-senha
SABEMI_API_KEY=defina-uma-chave-com-no-minimo-32-bytes
```

| Variável            | Descrição                                     |
| ------------------- | --------------------------------------------- |
| `POSTGRES_PASSWORD` | senha do PostgreSQL local                     |
| `SABEMI_API_KEY`    | chave das chamadas à API, **mínimo 32 bytes** |

Todos os endpoints, exceto `/health`, exigem o header `X-Api-Key`:

```powershell
$headers = @{ 'X-Api-Key' = '<SABEMI_API_KEY>' }
Invoke-RestMethod 'http://localhost:5070/pagamentos' -Headers $headers
```

## Painel web

Next.js em `http://localhost:3000`: lista os eventos, filtra por `status` e `id_contrato`, atualiza sozinho a cada 5s (ou pelo botão) e destaca em vermelho todo evento com falha, mostrando a causa.

A consulta roda no servidor do Next (`SABEMI_API_URL` + `SABEMI_API_KEY`), então a API key não vai ao navegador e a API não precisa de CORS. Fora do Docker:

```powershell
cd web
$env:SABEMI_API_URL = 'http://localhost:5070'
$env:SABEMI_API_KEY = '<SABEMI_API_KEY>'
npm install; npm run dev
```

## Endpoints

| Método | Rota                                                | Descrição                                 |
| ------ | --------------------------------------------------- | ----------------------------------------- |
| POST   | `/webhooks/pagamento`                               | Persiste a notificação antes de confirmar |
| GET    | `/pagamentos?status=&id_contrato=&pagina=&tamanho=` | Lista paginada, sem o corpo bruto         |
| GET    | `/pagamentos/{id}`                                  | Detalhes, corpo bruto e erro              |
| GET    | `/contratos/{idContrato}`                           | Último estado do contrato                 |
| GET    | `/health`                                           | 200 se o banco responder                  |
| GET    | `/openapi/v1.json`                                  | Documento OpenAPI                         |

POST: `202` novo · `200` reenvio equivalente · `400` inválido · `401` key inválida · `409` transação com conteúdo divergente · `413` corpo > 64 KiB · `415` Content-Type errado · `503` banco fora. Um `202` não significa processado — acompanhe o `eventId`.

## Regras

- `id_transacao` / `id_contrato`: 1–100 caracteres, sem espaços nas pontas.
- `valor`: decimal positivo, até 16 inteiros e 2 casas. `data_pagamento`: ISO 8601 com fuso, normalizada para UTC (precisão de microssegundos do PostgreSQL).
- Pagamento: `Pago`, `Recusado`, `Cancelado`. Processamento: `Pendente`, `Sucesso`, `Erro`, `Duplicado`.
- Índice único parcial em `TransactionId` (`Canonical = TRUE`): um aceite só, mesmo sob concorrência; reenvios ficam ligados ao original.
- Não são persistidas requisições sem autenticação, mídia errada, UTF-8 inválido ou acima de 64 KiB. JSON/campos inválidos ficam logados como `Erro`.
- Worker usa `FOR UPDATE SKIP LOCKED`; contrato e evento são atualizados na mesma transação e evento antigo não regride o contrato.
- Falha de processamento: até 3 tentativas com espera progressiva.

## Testes

```powershell
dotnet build .\Sabemi.sln
dotnet run --project .\tests\Sabemi.Checks
$env:SABEMI_TEST_CONNECTION = '<conexão de um banco de teste>'
dotnet run --project .\tests\Sabemi.Checks -- --integration
```

Cobrem payload válido/inválido, precisão monetária, fuso, chaves duplicadas, autenticação, 12 envios simultâneos, conflito, filtros, paginação, recuperação após reinício e OpenAPI.
