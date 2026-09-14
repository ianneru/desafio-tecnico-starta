import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "SABEMI · Painel de pagamentos",
  description: "Acompanhamento dos pagamentos recebidos por webhook.",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="pt-BR">
      <body>{children}</body>
    </html>
  );
}
