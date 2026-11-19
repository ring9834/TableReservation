import type { Locator } from "@playwright/test";
import jsQR from "jsqr";
import { PNG } from "pngjs";

/**
 * Reads the QR code an element shows, from its screenshot (M12b, Design §17.5): what a phone camera would see, so the
 * colours, the quiet zone and the module size are checked, not just the markup. `null` when nothing decodes.
 */
export const decodeQr = async (element: Locator): Promise<string | null> => {
  const png = PNG.sync.read(await element.screenshot());
  const pixels = new Uint8ClampedArray(png.data.buffer, png.data.byteOffset, png.data.byteLength);
  return jsQR(pixels, png.width, png.height, { inversionAttempts: "dontInvert" })?.data ?? null;
};
