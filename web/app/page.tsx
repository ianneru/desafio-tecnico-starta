import { Fragment } from "react";
import RefreshControls from "./refresh-controls";
import { fetchPayments, type PaymentEvent, type PaymentsPage } from "../lib/api";

export const dynamic = "force-dynamic";

const STATUS_OPTIONS = ["Pendente", "Sucesso", "Erro", "Duplicado"];
const PAGE_SIZE = 10;
const DATE_TIME = new Intl.DateTimeFormat("pt-BR", { dateStyle: "short", timeStyle: "medium" });
const MONEY = new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" });

type SearchParams = Promise<Record<string, string | string[] | undefined>>;

function first(value: string | string[] | undefined): string {
  return (Array.isArray(value) ? value[0] : value) ?? "";
}

function formatDate(value: string | null): string {
  return value ? DATE_TIME.format(new Date(value)) : "—";
}

function formatAmount(value: number | null): string {
  return value === null ? "—" : MONEY.format(value);
}

function StatusBadge({ status }: { status: string }) {
  return <span className={`badge badge-${status.toLowerCase()}`}>{status}</span>;
}

function pageHref(query: URLSearchParams, page: number): string {
  const params = new URLSearchParams(query);
  params.set("pagina", String(page));
  return `/?${params}`;
}

function PaymentsTable({ data }: { data: PaymentsPage }) {
  if (data.items.length === 0) {
    return <p className="empty">Nenhum evento encontrado para os filtros informados.</p>;
  }

  const failures = data.items.filter((item) => item.processingStatus === "Erro").length;

  return (
    <>
      {failures > 0 && (
        <div className="banner banner-danger" role="alert">
          <strong>⚠ {failures} evento(s) com falha nesta página.</strong>
          <span>Os registros rejeitados estão destacados em vermelho com a causa do erro.</span>
        </div>
      )}

      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Evento</th>
              <th>Transação</th>
              <th>Contrato</th>
              <th className="num">Valor</th>
              <th>Data pagamento</th>
              <th>Pagamento</th>
              <th>Processamento</th>
              <th className="num">Tentativas</th>
              <th>Recebido em</th>
            </tr>
          </thead>
          <tbody>
            {data.items.map((item: PaymentEvent) => {
              const failed = item.processingStatus === "Erro";
              return (
                <Fragment key={item.id}>
                  <tr className={failed ? "row-error" : undefined}>
                    <td className="mono" title={item.id}>
                      {item.id.slice(0, 8)}
                    </td>
                    <td className="mono">{item.transactionId ?? "—"}</td>
                    <td className="mono">{item.contractId ?? "—"}</td>
                    <td className="num">{formatAmount(item.amount)}</td>
                    <td>{formatDate(item.paidAt)}</td>
                    <td>{item.paymentStatus ?? "—"}</td>
                    <td>
                      <StatusBadge status={item.processingStatus} />
                    </td>
                    <td className="num">{item.attempts}</td>
                    <td>{formatDate(item.receivedAt)}</td>
                  </tr>
                  {failed && (
                    <tr className="row-error">
                      <td className="alert" colSpan={9}>
                        <span aria-hidden="true">⚠</span> {item.error ?? "Evento rejeitado."}
                        {item.originalEventId && (
                          <span className="muted"> · evento original {item.originalEventId.slice(0, 8)}</span>
                        )}
                      </td>
                    </tr>
                  )}
                </Fragment>
              );
            })}
          </tbody>
        </table>
      </div>
    </>
  );
}

export default async function Page({ searchParams }: { searchParams: SearchParams }) {
  const params = await searchParams;
  const status = first(params.status);
  const contractId = first(params.id_contrato).trim();
  const page = Math.max(1, Number.parseInt(first(params.pagina), 10) || 1);

  const result = await fetchPayments({ status, contractId, page, size: PAGE_SIZE });
  const query = new URLSearchParams();
  if (status) query.set("status", status);
  if (contractId) query.set("id_contrato", contractId);

  const pages = result.data ? Math.max(1, Math.ceil(result.data.total / PAGE_SIZE)) : 1;
  const renderedAt = new Date().toLocaleTimeString("pt-BR");

  return (
    <main>
      <header className="topbar">
        <div>
          <h1>Painel de pagamentos</h1>
          <p className="muted">
            Notificações recebidas via webhook.
            {result.data ? ` ${result.data.total} evento(s) no filtro atual.` : ""}
          </p>
        </div>
        <RefreshControls renderedAt={renderedAt} />
      </header>

      <form className="filters" method="get">
        <label>
          Status
          <select name="status" defaultValue={status}>
            <option value="">Todos</option>
            {STATUS_OPTIONS.map((option) => (
              <option key={option} value={option}>
                {option}
              </option>
            ))}
          </select>
        </label>
        <label>
          ID do contrato
          <input name="id_contrato" defaultValue={contractId} placeholder="contrato-123" autoComplete="off" />
        </label>
        <button type="submit">Filtrar</button>
        {(status || contractId) && (
          <a className="link" href="/">
            Limpar filtros
          </a>
        )}
      </form>

      {result.error ? (
        <div className="banner banner-danger" role="alert">
          <strong>Não foi possível consultar a API.</strong>
          <span>{result.error}</span>
        </div>
      ) : (
        <PaymentsTable data={result.data!} />
      )}

      {pages > 1 && (
        <nav className="pager">
          {page > 1 ? <a href={pageHref(query, page - 1)}>← Anterior</a> : <span className="disabled">← Anterior</span>}
          <span className="muted">
            Página {page} de {pages}
          </span>
          {page < pages ? <a href={pageHref(query, page + 1)}>Próxima →</a> : <span className="disabled">Próxima →</span>}
        </nav>
      )}
    </main>
  );
}
