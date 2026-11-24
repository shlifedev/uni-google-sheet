import { create } from "zustand";
import { rpc, RpcError, type HostEvent } from "./rpc";
import { setLanguage, T, type Language } from "./i18n";
import { defaults, savePreferences, withRecent, type Preferences } from "./lib/settings";
import type { AppInfo, ProjectState, RunResult, TableModel, HolySheetConfig } from "./types";

export type Page = "tables" | "settings";

export interface Notice {
  id: number;
  kind: "error" | "info";
  text: string;
}

interface AppState {
  info: AppInfo | null;
  preferences: Preferences;
  project: ProjectState | null;
  page: Page;
  running: boolean;
  run: RunResult | null;
  model: TableModel;
  revision: number;
  problemsOpen: boolean;
  notices: Notice[];

  setPreferences: (change: Partial<Preferences>) => void;
  setLanguage: (language: Language) => void;
  setPage: (page: Page) => void;
  setProblemsOpen: (open: boolean) => void;
  notify: (text: string, kind?: Notice["kind"]) => void;
  dismiss: (id: number) => void;

  openProject: (path: string) => Promise<void>;
  initProject: (dir: string, sample: boolean, unity?: boolean) => Promise<void>;
  closeProject: () => Promise<void>;
  reload: () => Promise<void>;
  saveConfig: (config: HolySheetConfig) => Promise<ProjectState | null>;
  applyState: (state: ProjectState) => void;
  startRun: (commit: boolean, only?: string[], options?: { offline?: boolean; refreshGoogle?: boolean; copy?: { workbook: string; sheet: string; useLocal: boolean } }) => Promise<void>;
  handleEvent: (event: HostEvent) => void;
  hostRestarted: () => Promise<void>;
}

const emptyModel: TableModel = { workbooks: [], tables: [], enums: [] };
let nextNotice = 1;

function message(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export const useApp = create<AppState>((set, get) => {
  const refreshModel = async () => {
    try {
      set({ model: await rpc.model() });
    } catch {
      set({ model: emptyModel });
    }
  };

  const afterOpen = async (state: ProjectState) => {
    set({ project: state, run: state.lastRun, running: state.running, model: emptyModel, problemsOpen: false });
    if (state.configPath) {
      get().setPreferences({ recent: withRecent(get().preferences.recent, state.configPath) });
    }
    if (state.config && !state.lastRun && state.canPreviewOffline) {
      await get().startRun(false, [], { offline: true });
    } else {
      await refreshModel();
    }
  };

  return {
    info: null,
    preferences: defaults,
    project: null,
    page: "tables",
    running: false,
    run: null,
    model: emptyModel,
    revision: 0,
    problemsOpen: false,
    notices: [],

    setPreferences: (change) => {
      const preferences = { ...get().preferences, ...change };
      set({ preferences });
      void savePreferences(preferences);
    },

    setLanguage: (language) => {
      setLanguage(language);
      get().setPreferences({ language });
    },

    setPage: (page) => set({ page }),
    setProblemsOpen: (problemsOpen) => set({ problemsOpen }),

    notify: (text, kind = "error") => {
      const id = nextNotice++;
      set({ notices: [...get().notices, { id, kind, text }] });
      setTimeout(() => get().dismiss(id), kind === "error" ? 8000 : 4000);
    },

    dismiss: (id) => set({ notices: get().notices.filter((notice) => notice.id !== id) }),

    openProject: async (path) => {
      try {
        await afterOpen(await rpc.open(path));
      } catch (error) {
        get().notify(message(error));
      }
    },

    initProject: async (dir, sample, unity) => {
      try {
        await afterOpen(await rpc.init(dir, sample, unity));
      } catch (error) {
        get().notify(message(error));
      }
    },

    closeProject: async () => {
      await rpc.close().catch(() => undefined);
      set({ project: null, running: false, run: null, model: emptyModel, page: "tables", problemsOpen: false });
    },

    reload: async () => {
      const { project, running } = get();
      if (!project?.dir || running) return;
      set({ running: true });
      try {
        const state = await rpc.state();
        if (get().project?.configPath !== project.configPath) return;
        if (!state.config) {
          set({ project: state, running: state.running, run: null, model: emptyModel });
          return;
        }
        set({ project: state });
        if (!state.running) await get().startRun(false, [], { refreshGoogle: true });
      } catch (error) {
        set({ running: false });
        get().notify(message(error));
      }
    },

    saveConfig: async (config) => {
      try {
        const state = await rpc.saveConfig(config);
        set({ project: state });
        return state;
      } catch (error) {
        get().notify(message(error));
        return null;
      }
    },

    applyState: (state) => set({ project: state }),

    startRun: async (commit, only = [], options = {}) => {
      try {
        const result = options.copy
          ? await rpc.localCopy(options.copy)
          : await rpc.run(commit, options.offline ?? false, only, options.refreshGoogle);
        get().handleEvent({ method: "run.finished", params: result });
      } catch (error) {
        if (error instanceof RpcError && error.code === RpcError.Busy) {
          get().notify(error.message, "info");
          return;
        }
        set({ running: false });
        get().notify(message(error));
      }
    },

    handleEvent: (event) => {
      if (event.method === "run.started") {
        set({ running: true });
        return;
      }

      const result = event.params;
      const previous = get().run;
      // The same run arrives twice (notification and response); only the first one changes anything.
      if (previous && previous.finishedAt === result.finishedAt && !get().running) return;
      set({
        running: false,
        run: result,
        revision: get().revision + 1,
        problemsOpen: get().problemsOpen || result.status === "failed" || result.status === "errors",
      });
      void refreshModel();
    },

    hostRestarted: async () => {
      const path = get().project?.configPath;
      set({ running: false });
      if (path) {
        get().notify(T("The engine process stopped and was started again."), "info");
        await get().openProject(path);
      }
    },
  };
});

export async function loadInfo(): Promise<void> {
  try {
    useApp.setState({ info: await rpc.info() });
  } catch (error) {
    useApp.getState().notify(message(error));
  }
}
