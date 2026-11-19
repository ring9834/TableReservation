"use client";

import { create } from "qrcode/lib/core/qrcode.js";
import { useMemo } from "react";
import { qrPath, viewBoxFor } from "./scanToBook.mjs";
import styles from "./scanToBook.module.css";

type Props = Readonly<{ url: string; label: string; className?: string; testId?: string }>;

/**
 * A QR code as an SVG: a white square with the quiet zone and one path for the dark modules. Drawn locally from
 * `qrcode`'s module matrix (no image service, no inline styles: the colours are CSS classes, Design §17.5).
 */
export function QrCode({ url, label, className, testId }: Props) {
  const { size, d } = useMemo(() => {
    const { modules } = create(url, { errorCorrectionLevel: "M" });
    return { size: modules.size, d: qrPath(modules.size, (row, col) => !!modules.get(row, col)) };
  }, [url]);
  const viewBox = viewBoxFor(size);
  const [x, y, width, height] = viewBox.split(" ");
  return (
    <svg
      viewBox={viewBox}
      role="img"
      aria-label={label}
      className={className ? `${styles.qr} ${className}` : styles.qr}
      data-testid={testId}
    >
      <rect x={x} y={y} width={width} height={height} className={styles.qrLight} />
      <path d={d} className={styles.qrDark} />
    </svg>
  );
}
