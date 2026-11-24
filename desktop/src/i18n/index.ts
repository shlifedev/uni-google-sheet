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

export function T(english: string): string {
  return effective === "ko" ? (korean[english] ?? english) : english;
}

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

export function Count(count: number, noun: string): string {
  const format = effective === "ko" ? koreanCounts[noun] : undefined;
  if (format) {
    return format.replace("{0}", formatNumber(count));
  }
  return `${formatNumber(count)} ${noun}${count === 1 ? "" : "s"}`;
}

export function koreanKeys(): Set<string> {
  return new Set(Object.keys(korean));
}
