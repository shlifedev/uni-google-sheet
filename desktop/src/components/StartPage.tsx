import { useState } from "react";
import { open } from "@tauri-apps/plugin-dialog";
import { useApp } from "../store";
import { T } from "../i18n";
import { openTarget, reveal } from "../lib/actions";

/** No project open: open a folder or a config file, or pick a recent one. */
export function StartPage() {
  const { preferences, openProject, info } = useApp();

  const openFolder = async () => {
    const picked = await open({ directory: true, title: T("Open a project folder") });
    if (typeof picked === "string") await openProject(picked);
  };

  const openConfig = async () => {
    const picked = await open({ title: T("Open a config file"), filters: [{ name: "ugs.json", extensions: ["json"] }] });
    if (typeof picked === "string") await openProject(picked);
  };

  return (
    <div className="start">
      <div className="start-card">
        <h1>UGS</h1>
        <p className="lead">{T("Game tables from xlsx, CSV and Google Sheets: typed C# and binary data, the same as the Unity window and the ugs command line.")}</p>
        <div className="actions">
          <button className="button primary" onClick={() => void openFolder()}>
            {T("Open folder…")}
          </button>
          <button className="button" onClick={() => void openConfig()}>
            {T("Open config file…")}
          </button>
        </div>
        {preferences.recent.length > 0 && (
          <>
            <div className="muted" style={{ marginBottom: 6 }}>
              {T("Recent")}
            </div>
            <div className="recent">
              {preferences.recent.map((path) => {
                const parts = path.split(/[/\\]/);
                return (
                  <div key={path} className="recent-item" onDoubleClick={() => void openProject(path)} onClick={() => void openProject(path)}>
                    <span>
                      {parts[parts.length - 2]} <span className="faint">/ {parts[parts.length - 1]}</span>
                    </span>
                    <span className="path ellipsis">{path}</span>
                  </div>
                );
              })}
            </div>
          </>
        )}
        {info && (
          <p className="faint" style={{ marginTop: 20, fontSize: 11 }}>
            {info.tool} · {info.runtime}
          </p>
        )}
      </div>
    </div>
  );
}

/** A project folder without a ugs.json, or with one that does not parse. */
export function NoConfigPage() {
  const { project, initProject, reload, closeProject } = useApp();
  const [sample, setSample] = useState(false);
  const [unity, setUnity] = useState(project?.unityProject ?? false);
  if (!project?.dir) return null;

  if (project.configError) {
    return (
      <div className="start">
        <div className="start-card callout error">
          <strong>{T("ugs.json could not be read")}</strong>
          <div className="selectable mono">{project.configError}</div>
          <div className="row">
            <button className="button" onClick={() => project.configPath && void openTarget(project.configPath)}>
              {T("Open")}
            </button>
            <button className="button" onClick={() => void reload()}>
              {T("Reload")}
            </button>
            <div className="fill" />
            <button className="button ghost" onClick={() => void closeProject()}>
              {T("Close project")}
            </button>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="start">
      <div className="start-card callout">
        <strong>{T("Create ugs.json")}</strong>
        <div className="muted">
          {T("This project has no ugs.json yet. It holds the sources and output folders; the ugs command line reads the same file.")}
        </div>
        <div className="mono faint selectable">{project.dir}</div>
        <label className="checkbox">
          <input type="checkbox" checked={sample} onChange={(event) => setSample(event.target.checked)} />
          {T("Add a small sample workbook (CSV) that builds")}
        </label>
        <label className="checkbox">
          <input type="checkbox" checked={unity} onChange={(event) => setUnity(event.target.checked)} />
          {T("Use output folders for a Unity project (Assets/…)")}
        </label>
        <div className="row">
          <button className="button primary" onClick={() => void initProject(project.dir!, sample, unity)}>
            {T("Create ugs.json")}
          </button>
          <button className="button" onClick={() => void reveal(project.dir!)}>
            {T("Reveal")}
          </button>
          <div className="fill" />
          <button className="button ghost" onClick={() => void closeProject()}>
            {T("Close project")}
          </button>
        </div>
      </div>
    </div>
  );
}
