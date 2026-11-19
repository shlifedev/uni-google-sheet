import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import type {
  AppInfo,
  Located,
  Preview,
  ProjectState,
  RunResult,
  TableModel,
  HolySheetConfig,
  VerifyReport,
} from "./types";

/** An error from the engine (its JSON-RPC error) or from the link to it. */
export class RpcError extends Error {
  constructor(
    public readonly code: number,
    message: string,
  ) {
    super(message);
  }

  static readonly HostUnavailable = -32000;
  static readonly ToolError = 1;
  static readonly Busy = 2;
  static readonly NoProject = 3;
}

async function call<T>(method: string, params: Record<string, unknown> = {}): Promise<T> {
  try {
    return await invoke<T>("rpc", { method, params });
  } catch (error) {
    if (error && typeof error === "object" && "code" in error && "message" in error) {
      throw new RpcError(Number(error.code), String(error.message));
    }
    throw new RpcError(RpcError.HostUnavailable, String(error));
  }
}

/** One method per engine call. The names match RpcServer.DispatchAsync. */
export const rpc = {
  info: () => call<AppInfo>("app.info"),
  open: (path: string) => call<ProjectState>("project.open", { path }),
  state: () => call<ProjectState>("project.state"),
  init: (dir: string, sample: boolean, unity?: boolean) => call<ProjectState>("project.init", { dir, sample, unity }),
  close: () => call<ProjectState>("project.close"),
  saveConfig: (config: HolySheetConfig) => call<ProjectState>("config.save", { config }),
  run: (commit: boolean, offline: boolean, only: string[] = []) => call<RunResult>("run", { commit, offline, only }),
  model: () => call<TableModel>("tables.model"),
  preview: (table: string, sheet?: string | null) => call<Preview>("tables.preview", { table, sheet: sheet ?? null }),
  locateSource: (workbook: string, sheet?: string | null) => call<Located>("source.locate", { workbook, sheet: sheet ?? null }),
  locateProblem: (location: string, workbook?: string, sheet?: string) =>
    call<Located>("problem.locate", { location, workbook: workbook ?? null, sheet: sheet ?? null }),
  toProjectPath: (path: string) => call<{ path: string; inside: boolean }>("path.toProject", { path }),
  setWatch: (enabled: boolean, offline: boolean, intervalSec: number) =>
    call<ProjectState>("watch.set", { enabled, offline, intervalSec }),
  createKeyPair: () => call<ProjectState>("signing.createKeyPair"),
  ignoreKey: () => call<ProjectState>("signing.ignoreKey"),
  stopSigning: () => call<ProjectState>("signing.stop"),
  verify: (dataDir?: string) => call<VerifyReport>("signing.verify", { dataDir: dataDir ?? null }),
};

export type HostEvent =
  | { method: "run.started"; params: { commit: boolean; offline: boolean; only: string[]; trigger: string } }
  | { method: "run.finished"; params: RunResult };

/** Notifications from the engine, and the shell's word that the engine was started again. */
export async function onHostEvents(handlers: {
  event: (event: HostEvent) => void;
  restarted: () => void;
}): Promise<UnlistenFn> {
  const unlisten = await Promise.all([
    listen<HostEvent>("holysheet://event", (message) => handlers.event(message.payload)),
    listen("holysheet://host-restarted", () => handlers.restarted()),
  ]);
  return () => unlisten.forEach((stop) => stop());
}
