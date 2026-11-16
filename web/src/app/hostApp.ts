import type { Metadata, Viewport } from "next";

// The host app, installable as a PWA (M11c, Design §18.11). Exported by /host/ and /demo-login/ only: guests
// (/book/, /manage/) aren't offered install, and the presenter runs in an ordinary laptop browser. The manifest's
// scope is "/" so that the redirect to /demo-login/ stays inside the installed app.

/** `--accent` in globals.css: the installed app's title bar and splash. */
const accent = "#9f1239";

export const hostAppMetadata: Metadata = {
  manifest: "/host.webmanifest",
  icons: { apple: "/icons/apple-touch-icon.png" },
  // Next writes `mobile-web-app-capable` for `capable`; older iOS still reads the `apple-` one.
  appleWebApp: { capable: true, title: "GD Host", statusBarStyle: "default" },
  other: { "apple-mobile-web-app-capable": "yes" },
};

/** Full screen to the edges; globals.css pads with the safe-area insets so nothing sits under a notch or home bar. */
export const hostAppViewport: Viewport = {
  themeColor: accent,
  viewportFit: "cover",
};
