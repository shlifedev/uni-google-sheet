// The app's state: the open project, the last run and the table model, plus what the toolbar toggles.
// The engine is the source of truth; this store only mirrors what it last said.
import { create } from "zustand";
import { rpc, RpcError, type HostEvent } from "./rpc";
import { setLanguage, T, type Language } from "./i18n";
import { defaults, savePreferences, withRecent, type Preferences } from "./lib/settings";
import type { AppInfo, ProjectState, RunResult, TableModel, HolySheetConfig } from "./types";

export type Page = "tables" | "settings";

/** A thing the user should know about that is not a sheet problem: the engine refused something, a file did not open. */
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
  /** Bumped on every finished run, so views that load from the engine (the preview) load again. */
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
  /** Offline follows the toolbar toggle unless given. */
  startRun: (commit: boolean, only?: string[], offline?: boolean) => Promise<void>;
  setOffline: (offline: boolean) => Promise<void>;
  setAutoBuild: (enabled: boolean) => Promise<void>;
  handleEvent: (event: HostEvent) => void;
  hostRestarted: () => Promise<void>;
}

const emptyModel: TableModel = { workbooks: [], tables: [], enums: [] };
let nextNotice = 1;

export function projectKey(state: ProjectState | null): string {
  return state?.configPath ?? "";
}

function message(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export const useApp = create<AppState>((set, get) => {
  const projectPreferences = () => get().preferences.projects[projectKey(get().project)] ?? {};

  const updateProjectPreferences = (change: Preferences["projects"][string]) => {
    const key = projectKey(get().project);
    if (!key) return;
    const projects = { ...get().preferences.projects, [key]: { ...projectPreferences(), ...change } };
    get().setPreferences({ projects });
  };

  const refreshModel = async () => {
    try {
      set({ model: await rpc.model() });
    } catch {
      set({ model: emptyModel });
    }
  };

  // After opening: restore the toolbar toggles and, as the Unity window does, read the sheets once by itself.
  const afterOpen = async (state: ProjectState) => {
    set({ project: state, run: state.lastRun, running: state.running, model: emptyModel, problemsOpen: false });
    if (state.configPath) {
      get().setPreferences({ recent: withRecent(get().preferences.recent, state.configPath) });
    }
    const saved = projectPreferences();
    if (state.config && saved.autoBuild) {
      set({ project: await rpc.setWatch(true, saved.offline ?? false, saved.intervalSec ?? 0) });
    }
    if (state.config && !state.lastRun) {
      // Offline when every Google source has a snapshot on disk, so opening does not wait on the network.
      const remote = state.config.sources.filter((source) => source.type === "google" || source.type === "googleFolder");
      const offline = saved.offline ?? (remote.length > 0 && remote.every((source) => !!source.snapshot));
      await get().startRun(false, [], offline);
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
      set({ project: null, run: null, model: emptyModel, page: "tables", problemsOpen: false });
    },

    reload: async () => {
      const path = get().project?.configPath;
      if (path) await get().openProject(path);
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

    startRun: async (commit, only = [], offline = projectPreferences().offline ?? false) => {
      try {
        // The result arrives through the run.finished notification as well; both lead to the same state.
        get().handleEvent({ method: "run.finished", params: await rpc.run(commit, offline, only) });
      } catch (error) {
        if (error instanceof RpcError && error.code === RpcError.Busy) {
          get().notify(error.message, "info");
          return;
        }
        set({ running: false });
        get().notify(message(error));
      }
    },

    setOffline: async (offline) => {
      updateProjectPreferences({ offline });
      const { project } = get();
      if (project?.watch.enabled) {
        set({ project: await rpc.setWatch(true, offline, projectPreferences().intervalSec ?? 0) });
      }
    },

    setAutoBuild: async (enabled) => {
      updateProjectPreferences({ autoBuild: enabled });
      try {
        set({ project: await rpc.setWatch(enabled, projectPreferences().offline ?? false, projectPreferences().intervalSec ?? 0) });
      } catch (error) {
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
        // The problems open by themselves when the run failed or the sheets have errors.
        problemsOpen: get().problemsOpen || result.status === "failed" || result.status === "errors",
      });
      void refreshModel();
    },

    hostRestarted: async () => {
      // A new engine process has nothing open: open the project again, and say why the view reset.
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
