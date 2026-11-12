"use client";

import { useEffect, useId, useRef, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import styles from "./actions.module.css";

type Props = Readonly<{
  title: string;
  onClose: () => void;
  children: ReactNode;
  testId?: string;
}>;

/**
 * A modal on the native `<dialog>` (Design §18.6): `showModal()` makes the rest of the page inert, keeps focus inside
 * and closes on Escape, with no extra package. Mounted = open; focus goes back to whatever opened it on close.
 * On a phone it fills the screen.
 */
export function Dialog({ title, onClose, children, testId }: Props) {
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
    dialog.addEventListener("cancel", handleCancel);
    return () => {
      dialog.removeEventListener("cancel", handleCancel);
      if (dialog.open) dialog.close();
      if (opener?.isConnected) opener.focus();
    };
  }, []);

  return (
    <dialog ref={ref} className={styles.dialog} aria-labelledby={headingId} data-testid={testId}>
      <div className={styles.dialogHeader}>
        <h2 id={headingId}>{title}</h2>
        <button type="button" className={styles.secondaryButton} onClick={onClose}>
          {t("host.panel.close")}
        </button>
      </div>
      {children}
    </dialog>
  );
}
