// Small helpers for editing holysheet.json in the Settings page; they follow the Unity window's rules.
import type { SheetRef, HolySheetConfig } from "../types";

/** The header layouts the window offers, by label. A config's own layout is kept when it is none of these. */
export const headerLayouts: { label: string; rows: string[] }[] = [
  { label: "type, name, comment", rows: ["type", "name", "comment"] },
  { label: "comment, name, type", rows: ["comment", "name", "type"] },
  { label: "name, type, comment", rows: ["name", "type", "comment"] },
  { label: "name, type", rows: ["name", "type"] },
  { label: "type, name", rows: ["type", "name"] },
];

export const csvBackends = ["auto", "builtin", "sep"];

/**
 * Takes a pasted URL or a bare id and returns the id: the part after /d/ (spreadsheets), /folders/ (Drive folders)
 * or ?id=. Anything that does not look like a URL is returned trimmed. Same as SourceConfig.ExtractGoogleId.
 */
export function extractGoogleId(text: string): string {
  const value = text.trim();
  for (const marker of ["/d/", "/folders/", "id="]) {
    const start = value.indexOf(marker);
    if (start >= 0) {
      const match = /^[A-Za-z0-9_-]+/.exec(value.slice(start + marker.length));
      if (match) return match[0];
    }
  }
  return value;
}

export function formatTabs(tabs: SheetRef[] | undefined): string {
  return (tabs ?? []).map((tab) => `${tab.name}:${tab.gid}`).join(", ");
}

/** Null when any entry is not a Name:gid pair. */
export function parseTabs(text: string): SheetRef[] | null {
  const tabs: SheetRef[] = [];
  for (const part of text.split(",")) {
    if (part.trim().length === 0) continue;
    const colon = part.lastIndexOf(":");
    const name = colon > 0 ? part.slice(0, colon).trim() : "";
    const gidText = colon > 0 ? part.slice(colon + 1).trim() : "";
    if (!name || !/^-?\d+$/.test(gidText)) return null;
    tabs.push({ name, gid: Number(gidText) });
  }
  return tabs;
}

/** An empty text means "not set": the key is left out of holysheet.json. */
export function optional(value: string): string | undefined {
  return value.trim().length > 0 ? value.trim() : undefined;
}

/** A copy to change and save; the engine parses it again before it is written. */
export function edit(config: HolySheetConfig, change: (next: HolySheetConfig) => void): HolySheetConfig {
  const next = structuredClone(config);
  change(next);
  return next;
}
