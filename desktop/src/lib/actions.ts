// Things the app asks the operating system to do: open a file in its program, open a link, show a file in
// Finder / Explorer, copy text. Failures become a notice instead of an unhandled rejection.
import { openPath, openUrl, revealItemInDir } from "@tauri-apps/plugin-opener";
import { rpc } from "../rpc";
import { useApp } from "../store";
import { F, T } from "../i18n";
import type { Diagnostic, Located } from "../types";

function fail(error: unknown) {
  useApp.getState().notify(error instanceof Error ? error.message : String(error));
}

export async function openTarget(target: string): Promise<void> {
  try {
    if (target.startsWith("https://")) await openUrl(target);
    else await openPath(target);
  } catch (error) {
    fail(error);
  }
}

export async function reveal(path: string): Promise<void> {
  try {
    await revealItemInDir(path);
  } catch (error) {
    fail(error);
  }
}

async function openLocated(located: Located, what: string) {
  if (!located.target) {
    useApp.getState().notify(F("The file of '{0}' was not found among the sources.", what), "info");
    return;
  }
  await openTarget(located.target);
}

/** Opens a workbook (a CSV workbook at the given sheet's file) in Excel, the text editor or the browser. */
export async function openSource(workbook: string, sheet?: string | null): Promise<void> {
  try {
    await openLocated(await rpc.locateSource(workbook, sheet), workbook);
  } catch (error) {
    fail(error);
  }
}

/** Opens the file a problem points at; problems about the config open holysheet.json. */
export async function openProblem(problem: Diagnostic): Promise<void> {
  try {
    await openLocated(await rpc.locateProblem(problem.location, problem.workbook, problem.sheet), problem.workbook ?? problem.location);
  } catch (error) {
    fail(error);
  }
}

export async function copy(text: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
    useApp.getState().notify(T("Copied."), "info");
  } catch (error) {
    fail(error);
  }
}

export const isMac = typeof navigator !== "undefined" && /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);

/** "⌘B" on macOS, "Ctrl+B" elsewhere. */
export function shortcut(key: string, shift = false): string {
  return isMac ? (shift ? "⇧⌘" : "⌘") + key : (shift ? "Ctrl+Shift+" : "Ctrl+") + key;
}
