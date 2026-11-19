// `qrcode`'s encoder on its own (M12b, Design §17.5): only `create`, without the package's canvas, PNG and string
// renderers, which its main entry pulls in (CommonJS, so a named import can't drop them). Typed by @types/qrcode.
declare module "qrcode/lib/core/qrcode.js" {
  export { create } from "qrcode";
}
