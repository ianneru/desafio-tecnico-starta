# SABEMI — API de notificações de pagamento

API REST em ASP.NET Core **.NET 10**, PostgreSQL e EF Core. Recebe webhooks autenticados, mantém log bruto de cada tentativa, impede aplicação duplicada e processa pagamentos em background. Inclui um painel web em Next.js para acompanhar os recebimentos.

## Executar com Docker

API: `http://localhost:5070`. Painel web: `http://localhost:3000`. O Compose aplica migrations ao iniciar e preserva dados no volume `postgres-data`. Não execute `docker compose down -v` se quiser preservar os dados. Para produção, aplique migrations como etapa separada do deploy e exponha a aplicação somente por HTTPS (por exemplo, via proxy reverso).

## Painel web (Next.js)

Painel em Next.js (App Router) que lista os eventos recebidos, filtra por `status` e por `id_contrato`, atualiza sozinho a cada 5 segundos (ou pelo botão "Atualizar agora") e destaca em vermelho, com a causa, todo evento rejeitado na validação. A lista é paginada.

A consulta à API é feita no servidor do Next (variáveis `SABEMI_API_URL` e `SABEMI_API_KEY`), então a API key **não** é enviada ao navegador e não é preciso configurar CORS na API.

O serviço `web` já faz parte do `compose.yaml`. Para rodar o painel fora do Docker:

```powershell
Set-Location <caminho-do-repositório>\web
$env:SABEMI_API_URL = 'http://localhost:5070'
$env:SABEMI_API_KEY = Read-Host 'API key (a mesma configurada na API)'
npm install
npm run dev
```

## Executar pelo SDK

Requer SDK .NET 10 e PostgreSQL 17 disponível. Configure um banco vazio e uma credencial com permissões de criação de tabelas para as migrations.

```powershell
Set-Location <caminho-do-repositório>
$env:ConnectionStrings__Payments = 'Host=localhost;Port=5432;Database=sabemi;Username=sabemi;Password=' + (Read-Host 'Senha do PostgreSQL')
$env:Security__ApiKey = Read-Host 'API key (mínimo 32 bytes)'
dotnet tool restore
dotnet ef database update --project .\src\Sabemi.Api
dotnet run --project .\src\Sabemi.Api --no-launch-profile --urls http://localhost:5070
```

Não existe senha/API key padrão no código. Configurações obrigatórias ausentes impedem a inicialização. `Database__ApplyMigrations=true` permite aplicação automática das migrations apenas quando desejado. `Worker__Enabled=false` desliga o consumidor, mantendo o recebimento persistente (útil nos testes de recuperação).

## Exemplo de webhook

```powershell
$headers = @{ 'X-Api-Key' = $env:Security__ApiKey }
$body = @{
    id_transacao = 'banco-tx-001'
    id_contrato = 'contrato-123'
    valor = 150.75
    data_pagamento = '2026-09-12T12:00:00-03:00'
    status = 'Pago'
} | ConvertTo-Json
Invoke-RestMethod http://localhost:5070/webhooks/pagamento -Method Post -Headers $headers -ContentType application/json -Body $body
Invoke-RestMethod 'http://localhost:5070/pagamentos?status=Sucesso&id_contrato=contrato-123' -Headers $headers
Invoke-RestMethod http://localhost:5070/contratos/contrato-123 -Headers $headers
```

### Contrato HTTP

Todos os endpoints, exceto `/health`, exigem `X-Api-Key`.

| POST | `/webhooks/pagamento` | Persiste a notificação antes de confirmar |
| GET | `/pagamentos?status=Erro&id_contrato=123&pagina=1&tamanho=20` | Lista recebimentos paginados, sem o corpo bruto |
| GET | `/pagamentos/{id}` | Detalhes, corpo bruto, erro e referência à tentativa original |
| GET | `/contratos/{idContrato}` | Último estado do contrato |
| GET | `/health` | 200 se o banco estiver acessível; 503 caso contrário |
| GET | `/openapi/v1.json` | Documento OpenAPI; sem interface Swagger adicional |

Respostas do POST: `202` novo evento persistido; `200` reenvio equivalente; `400` JSON/campos inválidos; `401` API key inválida; `409` mesma transação com conteúdo divergente; `413` corpo acima de 64 KiB; `415` Content-Type incorreto; `503` indisponibilidade de persistência. Erros usam Problem Details. Um `202` não significa pagamento já processado: consulte o `eventId` retornado.

## Regras e decisões feitos

- IDs são strings de 1 a 100 caracteres, sensíveis a maiúsculas, sem espaços nas extremidades.
- `valor` é decimal positivo, até 16 dígitos inteiros e 2 decimais. Não são calculados saldo, quitação ou estorno.
- `data_pagamento` é normalizada para UTC com precisão de microssegundos do PostgreSQL.
- Status de **pagamento** aceitos: `Pago`, `Recusado`, `Cancelado`.
- Status de **processamento**: `Pendente`, `Sucesso`, `Erro`, `Duplicado`.
- Enquanto o worker executa, o evento permanece publicamente `Pendente`: a aquisição é representada pelo bloqueio transacional, não por um estado `Processando` persistido que possa ficar abandonado.
- O primeiro evento válido cria o estado do contrato. Eventos mais antigos não o sobrescrevem: prevalece a maior tupla `(data_pagamento, recebimento, ID interno)`; assim o desempate é determinístico.
- Eventos autenticados com JSON inválido ou validação de campos inválida ficam no log como `Erro`. Identificadores extraíveis são preservados para filtros. Requisições não autenticadas, de mídia incorreta, UTF-8 inválido ou acima do limite não são persistidas.
- A lista permite filtros `Pendente`, `Sucesso`, `Erro`, `Duplicado`, páginas de 1 a 1.000.000 e tamanho de 1 a 100. O painel pode atualizar via polling/refresh.

## Persistência, idempotência e resiliência

`PaymentEvents` guarda o corpo recebido como texto (inclusive JSON malformado), os campos extraídos, erros, datas e tentativas. `ContractStatuses` guarda o último estado aplicado.

Um índice único parcial em `TransactionId`, condicionado a `Canonical = TRUE`, garante uma única transação aceita mesmo sob concorrência. Reenvios geram registros não canônicos ligados ao original, sem novo trabalho. A equivalência compara os cinco campos normalizados, não a formatação do JSON. Campos desconhecidos não participam da regra de negócio. Uma tentativa inválida não reserva o ID: ela pode ser corrigida e reenviada.

O `BackgroundService` consulta registros pendentes e usa `FOR UPDATE SKIP LOCKED`. A atualização do contrato e a conclusão do evento são atômicas na mesma transação. O processamento simula dois segundos com `Task.Delay`, sem bloquear o endpoint. A API não confirma recebimentos mantidos apenas em memória.

Se a aplicação cair, o PostgreSQL desfaz a transação e libera o evento para outra execução. Exceções de processamento registráveis têm até três tentativas, com espera progressiva de 5 e 10 segundos antes das novas tentativas. Indisponibilidade total do banco é retentada a cada cinco segundos pelo consumidor, pois não é possível gravar um contador enquanto o banco está inacessível.

**Limites intencionais:** um worker por processo, conexão/transação aberta durante os dois segundos; filas muito grandes ou tarefas externas longas pedem leases/outbox e consumidores dedicados. O efeito exatamente uma vez aqui é restrito às alterações transacionais no PostgreSQL; não se promete exactly-once para efeitos externos. Logs brutos precisam de política de retenção e proteção de dados antes de produção. A API key compartilhada é a segurança simples solicitada; um painel real deve usar autenticação própria, não expor a chave bancária no navegador. Não há endpoint manual de reprocessamento de falhas definitivas.

## Verificações automatizadas

O projeto `Sabemi.Checks` é um executável de assertions sem framework de testes adicional. Encerra com erro se uma verificação falhar.

```powershell
dotnet build .\Sabemi.sln
dotnet run --project .\tests\Sabemi.Checks
```

Para integração, use um **banco exclusivo de testes**, já criado e acessível. O executor aplica migrations, sobe uma API real em porta local livre, reinicia o processo e verifica o PostgreSQL. Os dados ficam no banco de testes com IDs únicos para inspeção; não use banco de produção.

```powershell
$env:SABEMI_TEST_CONNECTION = 'Host=localhost;Port=5432;Database=sabemi_tests;Username=sabemi;Password=' + (Read-Host 'Senha do PostgreSQL de testes')
dotnet run --project .\tests\Sabemi.Checks -- --integration
```

Cobertura: payload válido/inválido, precisão monetária, fuso horário, propriedades duplicadas, autenticação, 12 reenvios simultâneos, conflito de conteúdo, visibilidade de erros, paginação, recuperação após reinício, resposta sem esperar processamento, atualização do contrato sem regressão, idempotência após conclusão e OpenAPI.

Para executar o teste contra uma DLL publicada em outro local, configure `SABEMI_API_DLL` com caminho absoluto. `DOTNET_HOST_PATH` permite selecionar um host .NET específico.