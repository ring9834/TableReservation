"use client";

import { useEffect, useId, useRef } from "react";
import { useTranslation } from "react-i18next";
import { QrCode } from "./QrCode";
import styles from "./scanToBook.module.css";

type Props = Readonly<{ url: string; onClose: () => void }>;

/**
 * The "Scan to book" code full size (M12b, Design §17.5), on the native `<dialog>` like the host's `Dialog`
 * (`showModal()`: the page behind is inert, Escape closes). Closes with Escape, the Close button or a tap outside the
 * card (a click whose target is the dialog itself, i.e. its backdrop). Mounted = open; focus goes back to "Enlarge".
 */
export function QrOverlay({ url, onClose }: Props) {
  const { t } = useTranslation();
  const ref = useRef<HTMLDialogElement>(null);
  const headingId = useId();
  const onCloseRef = useRef(onClose);

  useEffect(() => {
    onCloseRef.current = onClose;
  }, [onClose]);

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    if (!dialog.open) dialog.showModal();
    const handleCancel = (event: Event) => {
      event.preventDefault(); // Escape: let React unmount it, so state and focus stay in step
      onCloseRef.current();
    };
    const handleClick = (event: MouseEvent) => {
      if (event.target === dialog) onCloseRef.current(); // the backdrop: the card fills the dialog's box
    };
    dialog.addEventListener("cancel", handleCancel);
    dialog.addEventListener("click", handleClick);
    return () => {
      dialog.removeEventListener("cancel", handleCancel);
      dialog.removeEventListener("click", handleClick);
      if (dialog.open) dialog.close();
      if (opener?.isConnected) opener.focus();
    };
  }, []);

  return (
    <dialog
      ref={ref}
      className={styles.overlay}
      aria-labelledby={headingId}
      data-testid="scan-overlay"
    >
      <div className={styles.overlayCard}>
        <div className={styles.overlayHeader}>
          <h2 id={headingId}>{t("presenter.scan.heading")}</h2>
          <button type="button" className={styles.button} onClick={onClose}>
            {t("presenter.scan.close")}
          </button>
        </div>
        <QrCode
          url={url}
          label={t("presenter.scan.qrLabel")}
          className={styles.qrLarge}
          testId="scan-qr-large"
        />
        <p className={styles.overlayUrl}>{url}</p>
      </div>
    </dialog>
  );
}
