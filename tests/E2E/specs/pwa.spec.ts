import {
  devices,
  expect,
  test,
  type APIRequestContext,
  type Browser,
  type BrowserContext,
  type Page,
} from "@playwright/test";
import { expectNoHorizontalScroll } from "../support/layout";
import { screenDevices } from "../support/screens";
import { hostSessionPath } from "../support/session";

// M11c (Design §18.11): the host app is installable as a PWA. The manifest and its icons, which pages link them, and
// the host page with an installed app's safe-area insets. Nothing signs in and nothing is booked.

type ManifestIcon = { src: string; sizes: string; type: string; purpose?: string };
type Manifest = Record<string, unknown> & {
  scope: string;
  start_url: string;
  icons: ManifestIcon[];
};

const hostAppPages = ["/host/", "/demo-login/"];
const guestPages = ["/book/", "/manage/"];

/** The `<link rel="manifest">` href in a page's HTML (the static export puts it in the head; next dev streams it). */
const manifestHref = (html: string) => {
  const link = html.match(/<link\b[^>]*\brel="manifest"[^>]*>/)?.[0];
  return link?.match(/\bhref="([^"]+)"/)?.[1] ?? null;
};

const pageHtml = async (request: APIRequestContext, path: string) => {
  const response = await request.get(path);
  expect(response.status(), `GET ${path}`).toBe(200);
  return response.text();
};

/** Width and height from a PNG's IHDR chunk (bytes 16–23, big-endian), after checking the signature. */
const pngSize = (bytes: Buffer) => {
  expect(bytes.subarray(0, 8).toString("hex"), "PNG signature").toBe("89504e470d0a1a0a");
  return `${bytes.readUInt32BE(16)}x${bytes.readUInt32BE(20)}`;
};

/** A URL is inside the manifest's scope when it's on the same origin and its path starts with the scope's path. */
const inScope = (url: string, scope: URL) => {
  const target = new URL(url);
  return target.origin === scope.origin && target.pathname.startsWith(scope.pathname);
};

const loadManifest = async (request: APIRequestContext, baseURL: string) => {
  const href = manifestHref(await pageHtml(request, "/host/"));
  expect(href, "/host/ links a manifest").not.toBeNull();
  const manifestUrl = new URL(href!, baseURL);
  const response = await request.get(manifestUrl.toString());
  expect(response.status()).toBe(200);
  return { manifestUrl, response, manifest: (await response.json()) as Manifest };
};

test.describe("the host app manifest (M11c)", () => {
  test("is served as a web app manifest with the agreed fields and icons", async ({ request, baseURL }) => {
    const { manifestUrl, response, manifest } = await loadManifest(request, baseURL!);
    expect(response.headers()["content-type"]).toMatch(/^application\/manifest\+json\b/);

    expect(manifest).toMatchObject({
      id: "/host/",
      name: "Golden Dragon Host",
      short_name: "GD Host",
      lang: "en",
      start_url: "/host/",
      scope: "/",
      display: "standalone",
      orientation: "any",
      theme_color: "#9f1239", // --accent
      background_color: "#ffffff", // --surface
    });

    // The scope keeps the start page and the sign-in in the app (on an iPad, an out-of-scope page opens in Safari).
    const scope = new URL(manifest.scope, manifestUrl);
    for (const path of [manifest.start_url, "/demo-login/", "/presenter/"]) {
      expect(inScope(new URL(path, manifestUrl).toString(), scope), `${path} in scope ${scope}`).toBe(true);
    }

    // Each icon exists, is a PNG and is the size it declares; installation needs 192 and 512, Android a maskable one.
    for (const icon of manifest.icons) {
      const image = await request.get(new URL(icon.src, manifestUrl).toString());
      expect(image.status(), icon.src).toBe(200);
      expect(image.headers()["content-type"], icon.src).toBe("image/png");
      expect(pngSize(await image.body()), icon.src).toBe(icon.sizes);
    }
    const declared = manifest.icons.map((icon) => `${icon.sizes} ${icon.purpose ?? "any"}`);
    expect(declared).toEqual(expect.arrayContaining(["192x192 any", "512x512 any", "512x512 maskable"]));
  });

  test("is linked from the host app pages with the iPad / iPhone tags", async ({ request }) => {
    for (const path of hostAppPages) {
      const html = await pageHtml(request, path);
      expect(manifestHref(html), path).toBe("/host.webmanifest");
      const tag = (pattern: RegExp) => expect(html, `${path}: ${pattern}`).toMatch(pattern);
      tag(/<meta\b[^>]*name="apple-mobile-web-app-capable"[^>]*content="yes"/);
      tag(/<meta\b[^>]*name="mobile-web-app-capable"[^>]*content="yes"/);
      tag(/<meta\b[^>]*name="apple-mobile-web-app-title"[^>]*content="GD Host"/);
      tag(/<meta\b[^>]*name="apple-mobile-web-app-status-bar-style"[^>]*content="default"/);
      tag(/<meta\b[^>]*name="theme-color"[^>]*content="#9f1239"/);
      tag(/<meta\b[^>]*name="viewport"[^>]*content="[^"]*viewport-fit=cover/);

      const appleIcon = html.match(/<link\b[^>]*rel="apple-touch-icon"[^>]*href="([^"]+)"/)?.[1];
      expect(appleIcon, `${path}: apple-touch-icon`).toBeDefined();
      const image = await request.get(appleIcon!);
      expect(image.status()).toBe(200);
      expect(pngSize(await image.body())).toBe("180x180");
    }
  });

  test("is not offered on the guest pages", async ({ request }) => {
    for (const path of guestPages) {
      const html = await pageHtml(request, path);
      expect(manifestHref(html), `${path} links no manifest`).toBeNull();
      expect(html, path).not.toMatch(/web-app-capable/);
      expect(html, path).not.toMatch(/viewport-fit=cover/);
    }
  });
});

// What the installed host app changes for the page, without standalone mode itself: Chromium can't be put into
// `display-mode: standalone` under automation (CDP's media emulation ignores it, and an `--app` window wasn't
// standalone on the development laptop's Edge), so real standalone is checked by hand on an iPad / iPhone after M12
// (Design §18.11). Here: the notch and home-bar insets an installed app gets (viewport-fit=cover), set through CDP, and
// the signed-out redirect, which must stay inside the manifest's scope. CDP is Chromium's, so the default project only.
test.describe("the host app's layout and sign-in redirect (M11c)", () => {
  test.skip(({ browserName }) => browserName !== "chromium", "the safe-area insets override is Chromium's (CDP)");

  /** iPad-like insets (status bar, home bar) on the tablet; an iPhone in landscape (notch at the side, home bar). */
  const insetsFor = {
    tablet: { top: 24, right: 0, bottom: 20, left: 0 },
    phone: { top: 0, right: 47, bottom: 21, left: 47 },
  };

  /** The project's tablet or phone, signed in with the shared host session when asked, with that device's insets. */
  const openDevice = async (
    browser: Browser,
    baseURL: string,
    device: "tablet" | "phone",
    signedIn: boolean,
  ): Promise<{ context: BrowserContext; page: Page; insets: (typeof insetsFor)["tablet"] }> => {
    const context = await browser.newContext({
      ...devices[screenDevices()[device]],
      baseURL,
      ignoreHTTPSErrors: true,
      storageState: signedIn ? hostSessionPath : undefined,
    });
    const page = await context.newPage();
    const insets = insetsFor[device];
    await (await context.newCDPSession(page)).send("Emulation.setSafeAreaInsetsOverride", { insets });
    return { context, page, insets };
  };

  for (const device of ["tablet", "phone"] as const) {
    test(`the host screen fits the ${device}, clear of the safe areas`, async ({ browser, baseURL }) => {
      const { context, page, insets } = await openDevice(browser, baseURL!, device, true);
      try {
        await page.goto("/host/");
        await expect(page.getByTestId("floor-plan")).toBeVisible();
        await expect(page.getByTestId("tonight-list")).toBeVisible();
        await expectNoHorizontalScroll(page, `host with safe-area insets, ${device}`);
        const padding = await page.evaluate(() => {
          const style = getComputedStyle(document.body);
          return [style.paddingTop, style.paddingRight, style.paddingBottom, style.paddingLeft];
        });
        expect(padding, "body padded by the safe-area insets").toEqual(
          [insets.top, insets.right, insets.bottom, insets.left].map((inset) => `${inset}px`),
        );
      } finally {
        await context.close();
      }
    });
  }

  test("signed out, the host app goes to the sign-in without leaving its scope", async ({
    browser,
    request,
    baseURL,
  }) => {
    const { manifestUrl, manifest } = await loadManifest(request, baseURL!);
    const scope = new URL(manifest.scope, manifestUrl);
    const { context, page } = await openDevice(browser, baseURL!, "tablet", false);
    try {
      const visited: string[] = [];
      page.on("framenavigated", (frame) => {
        if (frame === page.mainFrame()) visited.push(frame.url());
      });
      await page.goto(manifest.start_url);
      await expect(page).toHaveURL(/\/demo-login\/$/);
      await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
      expect(visited.length, "navigations seen").toBeGreaterThan(0);
      const outside = visited.filter((url) => !inScope(url, scope));
      expect(outside, `navigations outside ${scope}`).toEqual([]);
    } finally {
      await context.close();
    }
  });
});
