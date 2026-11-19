// The app's theme: data-theme on <html> picks the palette in styles.css, and the window's own theme makes the
// title bar match. "system" removes both, so the system decides.
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
    // Outside the app shell there is no window to set.
  }
}
