import type { TFunction } from "i18next";

/** "1 guest", "4 guests", "4 位". */
export const partyLabel = (t: TFunction, size: number) =>
  size === 1 ? t("when.partyOne") : t("when.partyMany", { count: size });
