// Per-user preferences, kept in the app's own settings file (not in the project): language, view options,
// recent projects, and per-project toggles. Read once at start; every change is saved at once.
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
  /** Per project path: the toolbar toggles and the auto-build poll interval. */
  projects: Record<string, { offline?: boolean; autoBuild?: boolean; intervalSec?: number }>;
}

export const defaults: Preferences = {
  language: "system",
  // Dark by default, like the editors this sits next to (Unity, an IDE).
  theme: "dark",
  gridLines: "both",
  alignment: "auto",
  stripes: true,
  findInAllColumns: false,
  recent: [],
  projects: {},
};

let store: Store | null = null;

export async function loadPreferences(): Promise<Preferences> {
  try {
    store = await load("settings.json", { autoSave: true, defaults: {} });
    const saved = await store.get<Partial<Preferences>>("preferences");
    return { ...defaults, ...(saved ?? {}) };
  } catch {
    // Outside the app shell (a plain browser, tests) there is no store: the defaults hold for the session.
    return { ...defaults };
  }
}

export async function savePreferences(preferences: Preferences): Promise<void> {
  try {
    await store?.set("preferences", preferences);
  } catch {
    // Not fatal: preferences are a convenience.
  }
}

/** The most recent first, at most ten, without duplicates. */
export function withRecent(recent: string[], path: string): string[] {
  return [path, ...recent.filter((entry) => entry !== path)].slice(0, 10);
}
