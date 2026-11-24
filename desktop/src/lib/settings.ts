import { load, type Store } from "@tauri-apps/plugin-store";
import type { Language } from "../i18n";

export type GridLines = "none" | "rows" | "columns" | "both";
export type Alignment = "auto" | "left" | "center" | "right";
export type Theme = "dark" | "light" | "system";

export interface Preferences {
  language: Language;
  theme: Theme;
  gridLines: GridLines;
  alignment: Alignment;
  stripes: boolean;
  findInAllColumns: boolean;
  recent: string[];
}

export const defaults: Preferences = {
  language: "system",
  theme: "dark",
  gridLines: "both",
  alignment: "auto",
  stripes: true,
  findInAllColumns: false,
  recent: [],
};

let store: Store | null = null;

export async function loadPreferences(): Promise<Preferences> {
  try {
    store = await load("settings.json", { autoSave: true, defaults: {} });
    const saved = await store.get<Partial<Preferences>>("preferences");
    return { ...defaults, ...(saved ?? {}) };
  } catch {
    return { ...defaults };
  }
}

export async function savePreferences(preferences: Preferences): Promise<void> {
  try {
    await store?.set("preferences", preferences);
  } catch {
  }
}

export function withRecent(recent: string[], path: string): string[] {
  return [path, ...recent.filter((entry) => entry !== path)].slice(0, 10);
}
