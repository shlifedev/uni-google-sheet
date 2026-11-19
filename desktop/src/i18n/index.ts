// Texts are written in English and looked up by that English text, as in the Unity window (UgsText.cs), so the two
// tools share one set of translations: ko.unity.json is exported from UgsText.cs (npm run i18n); ko.app.json holds
// what only the desktop app says. Engine messages (problems, hints, the run summary) stay in English, as in Unity.
import unity from "./ko.unity.json";
import app from "./ko.app.json";

export type Language = "system" | "en" | "ko";

const korean: Record<string, string> = { ...unity.texts, ...app.texts };
const koreanCounts: Record<string, string> = { ...unity.counts, ...app.counts };

let effective: "en" | "ko" = "en";

export function systemLanguage(): "en" | "ko" {
  const languages = typeof navigator !== "undefined" ? navigator.languages ?? [navigator.language] : [];
  return languages.some((language) => language?.toLowerCase().startsWith("ko")) ? "ko" : "en";
}

export function setLanguage(language: Language): void {
  effective = language === "system" ? systemLanguage() : language;
}

export function isKorean(): boolean {
  return effective === "ko";
}

/** The text in the current language. */
export function T(english: string): string {
  return effective === "ko" ? (korean[english] ?? english) : english;
}

/** A format string in the current language, with {0}, {1}... filled in. */
export function F(englishFormat: string, ...args: (string | number)[]): string {
  return T(englishFormat).replace(/\{(\d+)\}/g, (match, index: string) => {
    const value = args[Number(index)];
    return value === undefined ? match : String(value);
  });
}

const numberFormat = new Intl.NumberFormat("en-US");

export function formatNumber(value: number): string {
  return numberFormat.format(value);
}

/** "21 tables" / "테이블 21개". */
export function Count(count: number, noun: string): string {
  const format = effective === "ko" ? koreanCounts[noun] : undefined;
  if (format) {
    return format.replace("{0}", formatNumber(count));
  }
  return `${formatNumber(count)} ${noun}${count === 1 ? "" : "s"}`;
}

/** Every key with a Korean text, for the test that finds untranslated texts. */
export function koreanKeys(): Set<string> {
  return new Set(Object.keys(korean));
}
