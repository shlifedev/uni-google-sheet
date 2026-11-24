import { getCurrentWindow } from "@tauri-apps/api/window";
import type { Theme } from "./settings";

export function applyTheme(theme: Theme): void {
  if (theme === "system") delete document.documentElement.dataset.theme;
  else document.documentElement.dataset.theme = theme;

  try {
    void getCurrentWindow()
      .setTheme(theme === "system" ? null : theme)
      .catch(() => {});
  } catch {
  }
}
