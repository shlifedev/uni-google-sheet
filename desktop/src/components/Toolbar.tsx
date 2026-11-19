import { useApp, projectKey } from "../store";
import { useSelectionBuild } from "../selection";
import { Count, F, T } from "../i18n";
import { shortcut } from "../lib/actions";
import { showMenuBelow } from "./Menu";
import { MenuIcon } from "./Icons";
import { open } from "@tauri-apps/plugin-dialog";
import type { RunResult } from "../types";

/** "Built · 21 tables", "Failed · 3 errors", "Running": what the status says, as in the Unity window. */
export function statusText(run: RunResult | null, running: boolean): { text: string; tone: "ok" | "bad" | "busy" | "idle" } {
  if (running) return { text: T("Running"), tone: "busy" };
  if (!run) return { text: T("Not read yet"), tone: "idle" };
  if (run.status === "failed") return { text: T("Failed"), tone: "bad" };
  if (run.status === "errors") return { text: T("Failed") + " · " + Count(run.errors, "error"), tone: "bad" };

  const label = run.status === "checked" ? T("Checked") : run.status === "built" ? T("Built") : T("Up to date");
  const parts = [label];
  if (run.partial) parts.push(Count(run.refreshed.length, "sheet"));
  parts.push(Count(run.tables, "table"));
  if (run.warnings > 0) parts.push(Count(run.warnings, "warning"));
  return { text: parts.join(" · "), tone: "ok" };
}

export function Toolbar() {
  const { project, running, run, page, preferences, setPage, startRun, setOffline, setAutoBuild, setProblemsOpen, closeProject, openProject, reload } =
    useApp();
  const selection = useSelectionBuild();
  const saved = preferences.projects[projectKey(project)] ?? {};
  const hasConfig = !!project?.config;
  const status = statusText(run, running);
  const dir = project?.dir ?? "";
  const name = dir.split(/[/\\]/).filter(Boolean).pop() ?? "";
  const configName = project?.configPath?.split(/[/\\]/).pop() ?? "";

  const projectMenu = (element: HTMLElement) =>
    showMenuBelow(element, [
      {
        label: T("Open folder…"),
        action: async () => {
          const picked = await open({ directory: true });
          if (typeof picked === "string") await openProject(picked);
        },
      },
      {
        label: T("Open config file…"),
        action: async () => {
          const picked = await open({ filters: [{ name: "holysheet.json", extensions: ["json"] }] });
          if (typeof picked === "string") await openProject(picked);
        },
      },
      { label: T("Reload"), action: () => void reload() },
      ...(preferences.recent.filter((path) => path !== project?.configPath).length > 0
        ? [{ separator: true as const }, { heading: T("Recent") }]
        : []),
      ...preferences.recent
        .filter((path) => path !== project?.configPath)
        .slice(0, 6)
        .map((path) => ({ label: path.split(/[/\\]/).slice(-2).join("/"), action: () => void openProject(path) })),
      { separator: true },
      { label: T("Close project"), action: () => void closeProject() },
    ]);

  return (
    <div className="toolbar">
      <div className="project" title={project?.configPath ?? ""}>
        <span className="name ellipsis">{name}</span>
        <span className="path ellipsis">{configName}</span>
      </div>
      <button className="icon-button" title={T("Project")} onClick={(event) => projectMenu(event.currentTarget)}>
        <MenuIcon />
      </button>
      <div className="divider" />
      <div className="segmented" role="tablist">
        <button className={page === "tables" ? "on" : ""} onClick={() => setPage("tables")} role="tab">
          {T("Tables")}
        </button>
        <button className={page === "settings" ? "on" : ""} onClick={() => setPage("settings")} role="tab">
          {T("Settings")}
        </button>
      </div>
      <button
        className={`status ${status.tone}`}
        style={{ border: "none", background: "transparent" }}
        title={run?.summary ?? ""}
        onClick={() => {
          setPage("tables");
          setProblemsOpen(true);
        }}
      >
        <span className="dot" />
        <span className="ellipsis">{status.text}</span>
      </button>
      <div className="fill" />
      <button
        className={`toggle ${saved.offline ? "on" : ""}`}
        title={T("Build Google sources from the saved snapshot instead of the network.")}
        disabled={!hasConfig}
        onClick={() => void setOffline(!saved.offline)}
      >
        {T("Offline")}
      </button>
      <button
        className={`toggle ${project?.watch.enabled ? "on" : ""}`}
        title={
          T("Build whenever a local sheet file is saved.") +
          (project?.watch.enabled ? "\n" + F("Watching {0} folder(s).", project.watch.watching) : "")
        }
        disabled={!hasConfig}
        onClick={() => void setAutoBuild(!project?.watch.enabled)}
      >
        {T("Auto build")}
      </button>
      <div className="divider" />
      <button
        className="button"
        title={T("Read and validate the sheets. Writes nothing.") + ` (${shortcut("B", true)})`}
        disabled={!hasConfig || running}
        onClick={() => void startRun(false)}
      >
        {T("Check")}
      </button>
      {page === "tables" && !selection.empty && (
        <button
          className="button"
          title={selection.buildTooltip + "\n\n" + selection.label}
          disabled={!hasConfig || running}
          onClick={() => void startRun(true, selection.only)}
        >
          {F("Build selection ({0})", selection.count)}
        </button>
      )}
      <button
        className="button primary"
        title={T("Write generated code and data.") + ` (${shortcut("B")})`}
        disabled={!hasConfig || running}
        onClick={() => void startRun(true)}
      >
        {T("Build all")}
      </button>
    </div>
  );
}
