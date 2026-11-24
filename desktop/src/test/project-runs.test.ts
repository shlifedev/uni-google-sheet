import { beforeEach, expect, it, vi } from "vitest";
import { rpc } from "../rpc";
import { useApp } from "../store";
import type { ProjectState, RunResult, TableModel } from "../types";

vi.mock("../rpc", async (original) => {
  const actual = await original<typeof import("../rpc")>();
  return { ...actual, rpc: { ...actual.rpc, run: vi.fn(), model: vi.fn() } };
});

const project: ProjectState = {
  sessionId: "original", dir: "/game", configPath: "/game/holysheet.json", hasConfig: true, config: null,
  configError: null, unityProject: false, sources: [], keyFileExists: false, keyIgnored: false, running: false, lastRun: null,
};
const result: RunResult = {
  sessionId: project.sessionId, status: "errors", success: false, committed: false, failure: null, summary: "Errors",
  errors: 1, warnings: 0, tables: 0, rows: 0, enums: 0, partial: false, only: null, refreshed: [], csvBackend: "builtin",
  readMs: 0, buildMs: 0, written: [], deleted: [], unchanged: 0, finishedAt: "2026-01-01T00:00:00Z", diagnostics: [],
};
const empty: TableModel = { workbooks: [], tables: [], enums: [] };

beforeEach(() => {
  vi.resetAllMocks();
  useApp.setState({ project, run: null, running: false, model: empty, problemsOpen: false, revision: 0 });
});

it("ignores an old run's notifications and RPC response after reopening the same path", async () => {
  let finish!: (value: RunResult) => void;
  vi.mocked(rpc.run).mockReturnValue(new Promise((resolve) => { finish = resolve; }));
  const pending = useApp.getState().startRun(false);
  useApp.setState({ project: { ...project, sessionId: "reopened" } });
  useApp.getState().handleEvent({ method: "run.started", params: { sessionId: "original", commit: false, offline: false, only: [], trigger: "user" } });
  expect(useApp.getState().running).toBe(false);
  useApp.getState().handleEvent({ method: "run.finished", params: result });
  finish(result);
  await pending;
  expect(useApp.getState()).toMatchObject({ run: null, revision: 0, problemsOpen: false });
  expect(rpc.model).not.toHaveBeenCalled();
});

it("keeps the new project's model when an earlier model read finishes late", async () => {
  let finish!: (value: TableModel) => void;
  const pending = new Promise<TableModel>((resolve) => { finish = resolve; });
  vi.mocked(rpc.model).mockReturnValue(pending);
  useApp.getState().handleEvent({ method: "run.finished", params: result });
  useApp.setState({ project: { ...project, sessionId: "next" }, model: empty });
  finish({ ...empty, workbooks: [{ name: "OldWorkbook", type: "csv", source: "Sheets", link: null }] });
  await pending;
  expect(useApp.getState().model).toEqual(empty);
});
