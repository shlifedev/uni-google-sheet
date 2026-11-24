import { readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { Count, F, T, koreanKeys, setLanguage } from "../i18n";

function sources(directory: string): string[] {
  return readdirSync(directory).flatMap((name) => {
    const path = join(directory, name);
    if (statSync(path).isDirectory()) return name === "test" ? [] : sources(path);
    return /\.(ts|tsx)$/.test(name) ? [path] : [];
  });
}

export function usedTexts(): string[] {
  const found = new Set<string>();
  const call = /\b[TF]\(\s*"((?:[^"\\]|\\.)*)"/g;
  for (const file of sources(join(__dirname, ".."))) {
    for (const match of readFileSync(file, "utf8").matchAll(call)) found.add(JSON.parse(`"${match[1]}"`));
  }
  return [...found].sort();
}

describe("i18n", () => {
  it("translates every text the app uses", () => {
    const korean = koreanKeys();
    const missing = usedTexts().filter((text) => !korean.has(text));
    expect(missing).toEqual([]);
  });

  it("counts and formats in both languages", () => {
    setLanguage("en");
    expect(Count(1, "table")).toBe("1 table");
    expect(Count(21000, "row")).toBe("21,000 rows");
    expect(F("{0} of {1}", 3, 10)).toBe("3 of 10");
    setLanguage("ko");
    expect(Count(21, "table")).toBe("테이블 21개");
    expect(T("Tables")).toBe("테이블");
    expect(T("A text nobody translated")).toBe("A text nobody translated");
    setLanguage("en");
  });
});
