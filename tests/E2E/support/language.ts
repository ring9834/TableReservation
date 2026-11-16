import type { Page } from "@playwright/test";

/** One piece of visible UI text in the wrong language, with where it is. */
export type Offender = { found: string; text: string; element: string };

/**
 * Words that are right in Chinese UI text too (QA-7, M11b, Design §17.3): the venue's name, the currency, the names of
 * the POS messages, and the technical terms the Chinese strings keep on purpose (POS, JSON; checked in zh-CN.json).
 */
export const allowedLatinWords = [
  "Golden",
  "Dragon",
  "NZ",
  "POS",
  "JSON",
  "Stripe",
  "OpenTable",
  "UpsertMember",
  "ApplyDeposit",
  "BillClosed",
];

/**
 * Visible UI text of `page` in the wrong language for `language` (QA-7): Latin words on a Chinese screen, Chinese
 * characters on an English one. Looks at visible text nodes (SVG text included), the options of visible selects, and
 * at `aria-label`, `title`, `placeholder` and `alt`. Skips what is meant to be in another language: any element whose
 * `lang` differs from the page's (the language switch's other label, a text message in the guest's language, the POS
 * JSON), a text message's body and its links (the guest's text, not UI; the bubble's labels and action buttons are
 * still checked), URL-shaped text, and the `allowedNames` (guest names: data, not UI), also when cut short with "…" to
 * fit a tile. On a Chinese screen also skips the allowed words, booking references (GDB3K7) and table names (T4, W1).
 */
export const wrongLanguageText = (page: Page, language: "zh" | "en", allowedNames: string[]) =>
  page.evaluate(
    ({ language, allowedNames, allowedWords }) => {
      const pageLanguage = (document.documentElement.lang || "en").split("-")[0];
      const nameWords = allowedNames.flatMap((name) => name.split(/[\s·,()]+/)).map((w) => w.toLowerCase());
      const words = new Set([...allowedWords.map((w) => w.toLowerCase()), ...nameWords]);
      /**
       * A guest name too long for its tile is cut with "…" ("Em…" for Emma): the last word before the "…" is fine if it
       * starts a guest-name word. Only names: other cut words still count.
       */
      const cutNameWord = (word: string, text: string) => {
        const trimmed = text.trimEnd();
        if (!trimmed.endsWith("…")) return false;
        const stem = trimmed.slice(0, -1).trimEnd();
        return stem.endsWith(word) && nameWords.some((name) => name.startsWith(word.toLowerCase()));
      };
      const reference = /^[A-HJ-NP-Z2-9]{6}$/;
      const tableName = /^[TW]\d+$/;
      const latinWord = /[A-Za-z][A-Za-z0-9]*(?:['’.-][A-Za-z0-9]+)*/g;
      const chinese = /[\u3000-\u303f\u3400-\u9fff\uf900-\ufaff\uff00-\uffef]+/g;

      /**
       * URL-shaped text is not language: a link's address (shown short in a text message, e.g.
       * "localhost:3000/manage/?token=AQEx…") is dropped before looking for words.
       */
      const urlShaped = /(?:https?:\/\/)?(?:localhost|[\w-]+(?:\.[\w-]+)+)(?::\d+)?\/\S*/g;
      /**
       * A text message's body (and the links in it) is the guest's text as sent, not UI: skipped like the POS JSON. The
       * bubble's own labels (purpose, status, time) and its action buttons are UI and stay checked.
       */
      const inMessageBody = (element: Element) =>
        !!element.closest('[data-testid="phone-message"]') && !!element.closest("p[lang], a");
      const inOtherLanguage = (element: Element) => {
        const marked = element.closest("[lang]")?.getAttribute("lang");
        return !!marked && marked.split("-")[0] !== pageLanguage;
      };
      const visible = (element: Element) =>
        typeof element.checkVisibility === "function"
          ? element.checkVisibility({ opacityProperty: true, visibilityProperty: true })
          : element.getClientRects().length > 0;
      const describe = (element: Element) => {
        const tag = element.tagName.toLowerCase();
        const own = element.getAttribute("data-testid");
        const holder = element.closest("[data-testid]")?.getAttribute("data-testid");
        const heading = element.closest("section, dialog, [role=dialog]")?.querySelector("h1, h2, h3")?.textContent;
        return [
          own ? `${tag}[data-testid=${own}]` : holder ? `${tag} in [data-testid=${holder}]` : tag,
          heading ? `under “${heading.trim().slice(0, 40)}”` : "",
        ]
          .filter(Boolean)
          .join(" ");
      };
      const wrong = (raw: string) => {
        const text = raw.replace(urlShaped, " ");
        return language === "zh"
          ? (text.match(latinWord) ?? []).filter(
              (w) =>
                !words.has(w.toLowerCase()) && !reference.test(w) && !tableName.test(w) && !cutNameWord(w, text),
            )
          : (text.match(chinese) ?? []).filter((run) => !allowedNames.some((name) => name.includes(run)));
      };

      const offenders: { found: string; text: string; element: string }[] = [];
      const consider = (element: Element, text: string) => {
        const found = wrong(text);
        if (found.length > 0) {
          offenders.push({ found: found.join(" "), text: text.trim().slice(0, 80), element: describe(element) });
        }
      };

      const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
      for (let node = walker.nextNode(); node; node = walker.nextNode()) {
        const element = node.parentElement;
        const text = node.textContent ?? "";
        if (!element || text.trim() === "") continue;
        if (element.closest("script, style, noscript, template")) continue;
        if (inOtherLanguage(element) || inMessageBody(element) || !visible(element)) continue;
        consider(element, text);
      }
      // A closed <select> lays out none of its options: check them through the visible select.
      for (const select of document.body.querySelectorAll("select")) {
        if (inOtherLanguage(select) || !visible(select)) continue;
        for (const option of select.querySelectorAll("option")) {
          if (!inOtherLanguage(option) && option.textContent) consider(option, option.textContent);
        }
      }
      for (const element of document.body.querySelectorAll("[aria-label], [title], [placeholder], [alt]")) {
        if (inOtherLanguage(element) || inMessageBody(element) || !visible(element)) continue;
        for (const attribute of ["aria-label", "title", "placeholder", "alt"]) {
          const value = element.getAttribute(attribute);
          if (value) consider(element, value);
        }
      }
      return offenders;
    },
    { language, allowedNames, allowedWords: allowedLatinWords },
  );

/** Offenders as lines for a failure message: the word, the text it is in, and the element. */
export const describeOffenders = (offenders: Offender[]) =>
  offenders.map((o) => `“${o.found}” in “${o.text}” (${o.element})`);
