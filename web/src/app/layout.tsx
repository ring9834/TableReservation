import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "Golden Dragon reservations (demo)",
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
