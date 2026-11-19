import { useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import { loadInfo, useApp } from "./store";
import { onHostEvents } from "./rpc";
import { setLanguage } from "./i18n";
import { loadPreferences } from "./lib/settings";
import { applyTheme } from "./lib/theme";
import { useTablesView } from "./tablesView";
import { Toolbar } from "./components/Toolbar";
import { StartPage, NoConfigPage } from "./components/StartPage";
import { TablesPage } from "./components/TablesPage";
import { SettingsPage } from "./components/SettingsPage";
import { MenuHost } from "./components/Menu";
import { CloseIcon } from "./components/Icons";

function Notices() {
  const { notices, dismiss } = useApp();
  return (
    <div className="notices">
      {notices.map((notice) => (
        <div key={notice.id} className={`notice ${notice.kind}`}>
          <span className="fill selectable">{notice.text}</span>
          <button className="icon-button" onClick={() => dismiss(notice.id)}>
            <CloseIcon />
          </button>
        </div>
      ))}
    </div>
  );
}

// Once per app: React's development mode runs effects twice, and two opens would start two first checks.
let started = false;

export default function App() {
  const { project, page, preferences, handleEvent, hostRestarted, startRun, running } = useApp();

  // Start: preferences (language first, so the first paint is in the right one), the engine's info, the last project.
  // The event listeners stay for the app's lifetime.
  useEffect(() => {
    if (started) return;
    started = true;
    (async () => {
      const loaded = await loadPreferences();
      setLanguage(loaded.language);
      useApp.setState({ preferences: loaded });
      await onHostEvents({ event: handleEvent, restarted: () => void hostRestarted() });
      await loadInfo();
      const launched = await invoke<string | null>("launch_path").catch(() => null);
      const first = launched ?? loaded.recent[0];
      if (first) await useApp.getState().openProject(first);
    })();
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => applyTheme(preferences.theme), [preferences.theme]);

  // A different project starts the tree from scratch.
  useEffect(() => useTablesView.getState().reset(), [project?.configPath]);

  // ⌘B / Ctrl+B builds, ⇧⌘B / Ctrl+Shift+B checks.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (!(event.metaKey || event.ctrlKey) || event.key.toLowerCase() !== "b") return;
      event.preventDefault();
      if (project?.config && !running) void startRun(!event.shiftKey);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [project?.config, running, startRun]);

  let body: React.ReactNode;
  if (!project?.dir) body = <StartPage />;
  else if (!project.config) body = <NoConfigPage />;
  else body = page === "settings" ? <SettingsPage /> : <TablesPage />;

  return (
    // Every label is made in the language of the moment, so a language change draws the app again.
    <div className="app" key={preferences.language}>
      {project?.dir && <Toolbar />}
      <div className="fill">{body}</div>
      <MenuHost />
      <Notices />
    </div>
  );
}
