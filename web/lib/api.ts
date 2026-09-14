export type PaymentEvent = {
  id: string;
  transactionId: string | null;
  contractId: string | null;
  amount: number | null;
  paidAt: string | null;
  paymentStatus: string | null;
  processingStatus: string;
  error: string | null;
  attempts: number;
  receivedAt: string;
  completedAt: string | null;
  originalEventId: string | null;
};

export type PaymentsPage = {
  pagina: number;
  tamanho: number;
  total: number;
  items: PaymentEvent[];
};

export type PaymentsResult = { data?: PaymentsPage; error?: string };

const baseUrl = process.env.SABEMI_API_URL ?? "http://localhost:5070";

export async function fetchPayments(filters: {
  status?: string;
  contractId?: string;
  page?: number;
  size?: number;
}): Promise<PaymentsResult> {
  const params = new URLSearchParams();
  if (filters.status) params.set("status", filters.status);
  if (filters.contractId) params.set("id_contrato", filters.contractId);
  params.set("pagina", String(filters.page ?? 1));
  params.set("tamanho", String(filters.size ?? 10));

  try {
    const response = await fetch(`${baseUrl}/pagamentos?${params}`, {
      headers: { "X-Api-Key": process.env.SABEMI_API_KEY ?? "" },
      cache: "no-store",
    });

    if (!response.ok) {
      const detail = (await response.text()).slice(0, 300);
      return { error: `A API respondeu ${response.status}. ${detail}` };
    }

    return { data: (await response.json()) as PaymentsPage };
  } catch (cause) {
    return { error: `Falha de conexão com a API em ${baseUrl}. ${(cause as Error).message}` };
  }
}
