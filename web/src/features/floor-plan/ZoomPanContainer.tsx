"use client";

import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { TransformComponent, TransformWrapper } from "react-zoom-pan-pinch";
import styles from "./floorPlan.module.css";

/**
 * Zoom and pan for small screens (ADR-001, `react-zoom-pan-pinch`). At scale 1 the plan fits the width of its column
 * (a tablet sees the whole room) and panning is off, so a phone still scrolls the page; pinch, the wheel or the
 * buttons zoom in, and then one finger pans.
 */
export function ZoomPanContainer({ children }: Readonly<{ children: ReactNode }>) {
  const { t } = useTranslation();
  const [zoomed, setZoomed] = useState(false);

  return (
    <TransformWrapper
      minScale={1}
      maxScale={4}
      limitToBounds
      doubleClick={{ disabled: true }}
      panning={{ disabled: !zoomed, velocityDisabled: true }}
      onTransform={(_, state) => setZoomed(state.scale > 1.01)}
    >
      {({ zoomIn, zoomOut, resetTransform }) => (
        <div className={styles.zoomArea}>
          <div className={styles.zoomControls} role="group" aria-label={t("host.zoom.label")}>
            <button type="button" onClick={() => zoomIn()} aria-label={t("host.zoom.in")}>
              +
            </button>
            <button type="button" onClick={() => zoomOut()} aria-label={t("host.zoom.out")}>
              −
            </button>
            <button type="button" onClick={() => resetTransform()}>
              {t("host.zoom.fit")}
            </button>
          </div>
          <TransformComponent wrapperClass={styles.zoomWrapper} contentClass={styles.zoomContent}>
            {children}
          </TransformComponent>
        </div>
      )}
    </TransformWrapper>
  );
}
