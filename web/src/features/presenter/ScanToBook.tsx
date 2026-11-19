"use client";

import { useState, useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import { QrCode } from "./QrCode";
import { QrOverlay } from "./QrOverlay";
import { bookingUrl } from "./scanToBook.mjs";
import styles from "./scanToBook.module.css";

const noSubscription = () => () => {};

/** The page's origin in the browser; `null` while the static export is prerendered (no window). */
const useOrigin = () =>
  useSyncExternalStore(
    noSubscription,
    () => window.location.origin,
    () => null,
  );

/**
 * "Scan to book" on the presenter screen (M12b, Design §17.5): a QR code for this site's guest booking page, so the
 * audience can book on their own phone. Only the guest page, never a staff screen. "Enlarge" opens it full size.
 */
export function ScanToBook() {
  const { t } = useTranslation();
  const origin = useOrigin();
  const [enlarged, setEnlarged] = useState(false);
  if (!origin) return null;
  const url = bookingUrl(origin);

  return (
    <section className={styles.card} aria-labelledby="scan-heading" data-testid="scan-to-book">
      <QrCode url={url} label={t("presenter.scan.qrLabel")} testId="scan-qr" />
      <div className={styles.text}>
        <h2 id="scan-heading">{t("presenter.scan.heading")}</h2>
        <p className={styles.hint}>{t("presenter.scan.hint")}</p>
        <p className={styles.url} data-testid="scan-url">
          {url}
        </p>
        <button type="button" className={styles.button} onClick={() => setEnlarged(true)}>
          {t("presenter.scan.enlarge")}
        </button>
      </div>
      {enlarged && <QrOverlay url={url} onClose={() => setEnlarged(false)} />}
    </section>
  );
}
