import { useEffect, useState } from "react";
import { ask, message, open } from "@tauri-apps/plugin-dialog";
import { useApp } from "../store";
import { rpc } from "../rpc";
import { F, T, systemLanguage, type Language } from "../i18n";
import { openTarget, reveal } from "../lib/actions";
import type { Theme } from "../lib/settings";
import { csvBackends, edit, extractGoogleId, formatTabs, headerLayouts, optional, parseTabs } from "../lib/config";
import type { AuthType, SourceConfig, SourceState, HolySheetConfig, VerifyReport } from "../types";
import { NodeIcon, Chevron, CloseIcon, PlusIcon } from "./Icons";
import { showMenuBelow } from "./Menu";

type Browse = "file" | "folder" | "workbookOrFolder" | "keyFile";

// ---- Fields. Text saves when it loses focus or on Enter, so typing does not rewrite holysheet.json on every key. ----

// What a field is for is its tooltip, as in the Unity window: the page stays a list of fields.
function Row({ label, help, children }: { label: string; help?: string; children: React.ReactNode }) {
  return (
    <div className="form-row" title={help}>
      <label>{label}</label>
      <div className="controls">{children}</div>
    </div>
  );
}

// Which sections are open, for as long as the app runs.
const openSections = new Map<string, boolean>();

function Section({ id, title, state, open: initially, children }: { id: string; title: string; state?: string; open: boolean; children: React.ReactNode }) {
  const [open, setOpen] = useState(openSections.get(id) ?? initially);
  return (
    <div className="fold">
      <button
        className="fold-head"
        aria-expanded={open}
        onClick={() => {
          openSections.set(id, !open);
          setOpen(!open);
        }}
      >
        <span className={`twisty ${open ? "open" : ""}`}>
          <Chevron />
        </span>
        <span>{title}</span>
        {state && <span className="state">{state}</span>}
      </button>
      {open && <div className="fold-body">{children}</div>}
    </div>
  );
}

function TextField({
  value,
  onCommit,
  password,
  placeholder,
  readOnly,
  mono,
}: {
  value: string;
  onCommit: (value: string) => void;
  password?: boolean;
  placeholder?: string;
  readOnly?: boolean;
  mono?: boolean;
}) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  const commit = () => {
    if (draft !== value) onCommit(draft);
  };
  return (
    <input
      className={`field ${mono ? "mono" : ""}`}
      type={password ? "password" : "text"}
      value={draft}
      readOnly={readOnly}
      placeholder={placeholder}
      spellCheck={false}
      onChange={(event) => setDraft(event.target.value)}
      onBlur={commit}
      onKeyDown={(event) => {
        if (event.key === "Enter") (event.target as HTMLInputElement).blur();
        if (event.key === "Escape") setDraft(value);
      }}
    />
  );
}

function Select({ value, options, onChange }: { value: string; options: { value: string; label: string }[]; onChange: (value: string) => void }) {
  return (
    <select className="field" value={value} onChange={(event) => onChange(event.target.value)}>
      {options.map((option) => (
        <option key={option.value} value={option.value}>
          {option.label}
        </option>
      ))}
    </select>
  );
}

/** A path field with a browse button; what is picked is written relative to the project when it is inside it. */
function PathField({ value, onCommit, browse }: { value: string; onCommit: (value: string) => void; browse: Browse }) {
  const pick = async (directory: boolean) => {
    const filters =
      browse === "workbookOrFolder" ? [{ name: "Excel", extensions: ["xlsx"] }] : browse === "keyFile" ? [{ name: "JSON / key", extensions: ["json", "key", "txt"] }] : undefined;
    const picked = await open({ directory, filters });
    if (typeof picked !== "string") return;
    const { path } = await rpc.toProjectPath(picked);
    onCommit(path);
  };
  return (
    <>
      <TextField value={value} onCommit={onCommit} mono />
      {browse === "workbookOrFolder" ? (
        <>
          <button className="button small" onClick={() => void pick(false)}>
            {T("File…")}
          </button>
          <button className="button small" onClick={() => void pick(true)}>
            {T("Folder…")}
          </button>
        </>
      ) : (
        <button className="button small" onClick={() => void pick(browse === "folder")}>
          {T("Browse…")}
        </button>
      )}
    </>
  );
}

function Check({ checked, onChange, label }: { checked: boolean; onChange: (checked: boolean) => void; label?: string }) {
  return (
    <label className="checkbox">
      <input type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} />
      {label}
    </label>
  );
}

// ---- Page ----

export function SettingsPage() {
  const { project, info, saveConfig, preferences, setPreferences, setLanguage, applyState, notify } = useApp();
  const [report, setReport] = useState<VerifyReport | null>(null);
  const config = project?.config;
  if (!project || !config) return null;

  const save = (change: (next: HolySheetConfig) => void) => void saveConfig(edit(config, change));
  const signed = !!config.signing?.publicKey;
  const header = config.sheet.header.join(", ");
  const layouts = headerLayouts.map((layout) => layout.label);
  if (!layouts.includes(header)) layouts.push(header);
  const serializers = info?.serializers ?? ["memorypack", "messagepack"];

  const addSource = (element: HTMLElement) =>
    showMenuBelow(element, [
      { label: T("Excel workbook"), action: () => save((next) => next.sources.push({ type: "xlsx", path: "Sheets" })) },
      { label: T("CSV folder"), action: () => save((next) => next.sources.push({ type: "csv", path: "Sheets/Csv" })) },
      { label: "Google Sheets", action: () => save((next) => next.sources.push({ type: "google", spreadsheetId: "", auth: { type: "public" } })) },
      // A folder can only be listed with credentials, so it starts on an API key rather than on "public".
      { label: T("Google Drive folder"), action: () => save((next) => next.sources.push({ type: "googleFolder", folderId: "", auth: { type: "apiKey" } })) },
    ]);

  const createKeyPair = async () => {
    try {
      const state = await rpc.createKeyPair();
      applyState(state);
      if (!state.keyIgnored) {
        const add = await ask(T("The private key was written to holysheet.private.key. It must not be committed.\n\nAdd it to .gitignore?"), {
          title: "HolySheet",
          okLabel: T("Add"),
          cancelLabel: T("Not now"),
        });
        if (add) applyState(await rpc.ignoreKey());
      }
    } catch (error) {
      await message(error instanceof Error ? error.message : String(error), { title: "HolySheet", kind: "warning" });
    }
  };

  const verify = async () => {
    try {
      setReport(await rpc.verify());
    } catch (error) {
      notify(error instanceof Error ? error.message : String(error));
    }
  };

  const languageNames: { value: Language; label: string }[] = [
    { value: "system", label: F("System ({0})", systemLanguage() === "ko" ? "한국어" : "English") },
    { value: "en", label: "English" },
    { value: "ko", label: "한국어" },
  ];

  return (
    <div className="settings">
      <div className="settings-inner">
        {project.diagnostics && project.diagnostics.length > 0 && (
          <div className="callout error" style={{ margin: 10 }}>
            {project.diagnostics.map((problem, index) => (
              <div key={index}>
                <strong>{problem.location}</strong>: {problem.message} {problem.hint && <span className="muted">({problem.hint})</span>}
              </div>
            ))}
          </div>
        )}

        <Section id="sources" title={T("Sources")} open={true}>
          {config.sources.map((source, index) => (
            <SourceCard
              key={index}
              source={source}
              state={project.sources[index]}
              onChange={(change) => save((next) => change(next.sources[index]))}
              onRemove={() => save((next) => next.sources.splice(index, 1))}
            />
          ))}
          <button className="button" onClick={(event) => addSource(event.currentTarget)}>
            <PlusIcon /> {T("Add source")}
          </button>
        </Section>

        <Section id="output" title={T("Output")} open={true}>
          <div className="panel">
            <Row label={T("Code")} help={T("Generated .g.cs files. Keep it under Assets so Unity compiles them.")}>
              <PathField value={config.output.code} browse="folder" onCommit={(value) => save((next) => (next.output.code = value))} />
            </Row>
            <Row label={T("Data")} help={T(".bytes files and the manifest. Under StreamingAssets they stay plain files that can be watched and patched.")}>
              <PathField value={config.output.data} browse="folder" onCommit={(value) => save((next) => (next.output.data = value))} />
            </Row>
            <Row label={T("Transformed CSV")} help={T("Optional. Each table as CSV after the sheet rules, for review.")}>
              <PathField value={config.output.csv ?? ""} browse="folder" onCommit={(value) => save((next) => (next.output.csv = optional(value)))} />
            </Row>
            <Row label={T("Write schema file")} help={T("tables.schema.json next to the data, for tools without generated code.")}>
              <Check checked={config.output.schema} onChange={(value) => save((next) => (next.output.schema = value))} />
            </Row>
            <Row label={T("Namespace")}>
              <TextField value={config.codegen.namespace} onCommit={(value) => save((next) => (next.codegen.namespace = value))} mono />
            </Row>
            <Row label={T("Tables class")} help={T("The class that holds every table: Load, LoadAsync, Watch.")}>
              <TextField value={config.codegen.tablesClass} onCommit={(value) => save((next) => (next.codegen.tablesClass = value))} mono />
            </Row>
            <Row label={T("Serializer")} help={T("memorypack: fastest to load, C# only.\nmessagepack: readable from other languages.")}>
              <Select
                value={config.codegen.serializer}
                options={serializers.map((name) => ({ value: name, label: name }))}
                onChange={(value) => save((next) => (next.codegen.serializer = value))}
              />
            </Row>
          </div>
        </Section>

        <Section id="build" title={T("Sheet and build options")} open={false}>
          <div className="panel">
            <Row label={T("Header rows")} help={T("What the header rows of a table sheet hold, top to bottom. Data starts on the next row.")}>
              <Select
                value={header}
                options={layouts.map((label) => ({ value: label, label }))}
                onChange={(value) => {
                  const layout = headerLayouts.find((entry) => entry.label === value);
                  if (layout) save((next) => (next.sheet.header = [...layout.rows]));
                }}
              />
            </Row>
            <Row label={T("Array separator")} help={T("Separates array elements in one cell. [a, b] is accepted too.")}>
              <div style={{ width: 60 }}>
                <TextField
                  value={config.sheet.arraySeparator}
                  onCommit={(value) => value.length > 0 && save((next) => (next.sheet.arraySeparator = value[0]))}
                  mono
                />
              </div>
            </Row>
            <Row
              label={T("CSV reader")}
              help={info?.sepAvailable === false ? (info.sepUnavailableReason ?? "") : T("auto and builtin use the built-in reader; sep uses Sep. Same results.")}
            >
              <Select
                value={config.csv?.backend ?? "auto"}
                options={csvBackends.map((name) => ({ value: name, label: name }))}
                onChange={(value) => save((next) => (next.csv = { backend: value }))}
              />
            </Row>
            <Row label={T("Parallel")} help={T("Convert and encode tables on several threads. The output is the same either way.")}>
              <Check checked={config.build?.parallel ?? true} onChange={(value) => save((next) => (next.build = { ...next.build, parallel: value }))} />
            </Row>
            <Row label={T("Build label")} help={T("Optional text written to the manifest, such as a version.")}>
              <TextField value={config.build?.label ?? ""} onCommit={(value) => save((next) => (next.build = { ...next.build, label: optional(value) }))} />
            </Row>
          </div>
        </Section>

        <Section id="signing" title={signed ? T("Signing (signed builds)") : T("Signing (unsigned builds)")} open={false}>
          <div className="panel">
            {signed && (
              <Row label={T("Public key")} help={T("Baked into the generated code. Safe to ship.")}>
                <TextField value={config.signing?.publicKey ?? ""} onCommit={() => undefined} readOnly mono />
              </Row>
            )}
            <Row label={T("Private key file")} help={T("Signs the manifest. Keep it out of version control.")}>
              <PathField
                value={config.signing?.privateKeyFile ?? ""}
                browse="file"
                onCommit={(value) => save((next) => (next.signing = { ...next.signing, privateKeyFile: optional(value) }))}
              />
            </Row>
            {signed && config.signing?.privateKeyFile && !project.keyFileExists && (
              <div className="form-row">
                <span />
                <span className="warning-text">{T("The private key file is not there; builds cannot sign until it is.")}</span>
              </div>
            )}
            {signed && project.keyFileExists && !project.keyIgnored && (
              <div className="form-row">
                <span />
                <div className="row">
                  <span className="warning-text">{T("The private key is not in .gitignore.")}</span>
                  <button className="button small" onClick={async () => applyState(await rpc.ignoreKey())}>
                    {T("Add to .gitignore")}
                  </button>
                </div>
              </div>
            )}
            <Row label={T("Private key variable")} help={T("Environment variable with the private key text, for CI. Wins over the file.")}>
              <TextField
                value={config.signing?.privateKeyEnv ?? ""}
                onCommit={(value) => save((next) => (next.signing = { ...next.signing, privateKeyEnv: optional(value) }))}
                mono
              />
            </Row>
            <div className="form-row">
              <span />
              <div className="row">
                {signed ? (
                  <button className="button" onClick={async () => applyState(await rpc.stopSigning())}>
                    {T("Stop signing")}
                  </button>
                ) : (
                  <button className="button" onClick={() => void createKeyPair()}>
                    {T("Create key pair")}
                  </button>
                )}
                <button className="button" title={T("Check every data file against the manifest, and the signature if there is one.")} onClick={() => void verify()}>
                  {T("Verify data folder")}
                </button>
              </div>
            </div>
            {report && <VerifyResult report={report} />}
          </div>
        </Section>

        <Section id="app" title={T("App")} open={true}>
          <div className="panel">
            <Row label={T("Language")} help={T("The language of this app. Kept for you only.")}>
              <Select value={preferences.language} options={languageNames} onChange={(value) => setLanguage(value as Language)} />
            </Row>
            <Row label={T("Theme")} help={T("The colors of this app. Kept for you only.")}>
              <Select
                value={preferences.theme}
                options={[
                  { value: "dark", label: T("Dark") },
                  { value: "light", label: T("Light") },
                  { value: "system", label: T("Follow the system") },
                ]}
                onChange={(value) => setPreferences({ theme: value as Theme })}
              />
            </Row>
            <Row label={T("Auto build interval")} help={T("Auto build reads Google sources again this often (seconds). 0: only local files trigger a build.")}>
              <div style={{ width: 90 }}>
                <IntervalField />
              </div>
            </Row>
            {info && (
              <Row label={T("Engine")}>
                <span className="muted selectable">
                  {info.tool} · {info.runtime} · CSV {info.csvBackends.join(", ")}
                </span>
              </Row>
            )}
          </div>
        </Section>

        <div className="settings-foot">
          <button className="button" onClick={() => project.configPath && void openTarget(project.configPath)}>
            {T("Open holysheet.json")}
          </button>
          <button className="button" onClick={() => project.configPath && void reveal(project.configPath)}>
            {T("Reveal")}
          </button>
        </div>
      </div>
    </div>
  );
}

function IntervalField() {
  const { project, preferences, setPreferences, setAutoBuild } = useApp();
  const key = project?.configPath ?? "";
  const current = preferences.projects[key]?.intervalSec ?? 0;
  return (
    <TextField
      value={String(current)}
      onCommit={(value) => {
        const seconds = Math.max(0, Math.floor(Number(value) || 0));
        setPreferences({ projects: { ...preferences.projects, [key]: { ...preferences.projects[key], intervalSec: seconds } } });
        if (project?.watch.enabled) void setAutoBuild(true);
      }}
    />
  );
}

function VerifyResult({ report }: { report: VerifyReport }) {
  // The same sentences as the Unity window's report.
  const lines = report.success
    ? [F(report.signature === "valid" ? "{0} files match the manifest; the signature is valid." : "{0} files match the manifest.", report.tables)]
    : report.problems;
  return (
    <div style={{ borderTop: "1px solid var(--border)" }}>
      <pre className={`report selectable ${report.success ? "ok" : "bad"}`}>{lines.join("\n")}</pre>
      <div className="faint mono" style={{ padding: "0 14px 10px" }}>
        {report.directory} · {T("signature")}: {report.signature}
      </div>
    </div>
  );
}

const authTypes: AuthType[] = ["public", "apiKey", "serviceAccount", "accessToken", "custom", "command"];

function authName(type: AuthType): string {
  switch (type) {
    case "public":
      return T("Public link");
    case "apiKey":
      return T("API key");
    case "serviceAccount":
      return T("Service account");
    case "accessToken":
      return T("Access token");
    case "custom":
      return T("Custom provider");
    case "command":
      return T("Command");
  }
}

function SourceCard({
  source,
  state,
  onChange,
  onRemove,
}: {
  source: SourceConfig;
  state: SourceState | undefined;
  onChange: (change: (source: SourceConfig) => void) => void;
  onRemove: () => void;
}) {
  const { notify } = useApp();
  const remote = source.type === "google" || source.type === "googleFolder";
  const folder = source.type === "googleFolder";
  const auth = source.auth ?? { type: "public" as AuthType };
  const setAuth = (change: (auth: NonNullable<SourceConfig["auth"]>) => void) =>
    onChange((next) => {
      next.auth = { ...(next.auth ?? { type: "public" }) };
      change(next.auth);
    });

  const title = source.type === "google" ? "Google Sheets" : folder ? T("Google Drive folder") : source.type === "csv" ? T("CSV folder") : T("Excel workbook");
  const icon = source.type === "google" ? "sheets" : folder ? "drive" : source.type === "csv" ? "folder" : "excel";
  const summary = source.type === "google" ? (source.name ?? source.spreadsheetId) : folder ? (source.name ?? source.folderId) : source.path;

  return (
    <div className="source-card">
      <div className="head">
        <NodeIcon name={icon} />
        <span className="title">{title}</span>
        <span className="faint ellipsis fill mono">{summary}</span>
        {state?.link && (
          <button className="button small ghost" onClick={() => void openTarget(state.link!)}>
            {T("Open")}
          </button>
        )}
        <button className="icon-button" title={T("Remove this source")} onClick={onRemove}>
          <CloseIcon />
        </button>
      </div>

      {!remote && (
        <>
          <Row
            label={source.type === "csv" ? T("Folder") : T("File or folder")}
            help={source.type === "csv" ? T("A folder of .csv files. Each file is a sheet.") : T("An .xlsx file, or a folder whose .xlsx files are all read.")}
          >
            <PathField
              value={source.path ?? ""}
              browse={source.type === "csv" ? "folder" : "workbookOrFolder"}
              onCommit={(value) => onChange((next) => (next.path = value))}
            />
          </Row>
          {state && !state.exists && (
            <div className="form-row">
              <span />
              <span className="warning-text selectable">
                {T("Not found: ")}
                {state.resolved}
              </span>
            </div>
          )}
        </>
      )}

      {remote && (
        <>
          {folder ? (
            <Row
              label={T("Folder")}
              help={T(
                "The id from drive.google.com/drive/folders/<id>, or paste the whole URL. Every Google spreadsheet and .xlsx file directly in the folder is read; names starting with # are skipped.",
              )}
            >
              <TextField value={source.folderId ?? ""} onCommit={(value) => onChange((next) => (next.folderId = extractGoogleId(value)))} mono />
            </Row>
          ) : (
            <Row label={T("Spreadsheet")} help={T("The id from the URL, or paste the whole URL.")}>
              <TextField value={source.spreadsheetId ?? ""} onCommit={(value) => onChange((next) => (next.spreadsheetId = extractGoogleId(value)))} mono />
            </Row>
          )}
          <Row
            label={T("Access")}
            help={T("Public link and API key read spreadsheets shared as 'anyone with the link'. A service account reads private ones shared with its e-mail.")}
          >
            <Select
              value={auth.type}
              options={authTypes.map((type) => ({ value: type, label: authName(type) }))}
              onChange={(value) => setAuth((next) => (next.type = value as AuthType))}
            />
          </Row>
          {auth.type === "apiKey" && (
            <>
              <Row label={T("Key variable")} help={T("Environment variable that holds the API key.")}>
                <TextField value={auth.apiKeyEnv ?? ""} onCommit={(value) => setAuth((next) => (next.apiKeyEnv = optional(value)))} mono />
              </Row>
              <Row label={T("Key")} help={T("Or the key itself. Saved to holysheet.json as plain text.")}>
                <TextField value={auth.apiKey ?? ""} onCommit={(value) => setAuth((next) => (next.apiKey = optional(value)))} password />
              </Row>
            </>
          )}
          {auth.type === "serviceAccount" && (
            <Row label={T("Key file")} help={T("The service account's JSON key. Keep it out of version control.")}>
              <PathField value={auth.keyFile ?? ""} browse="keyFile" onCommit={(value) => setAuth((next) => (next.keyFile = optional(value)))} />
            </Row>
          )}
          {auth.type === "accessToken" && (
            <Row label={T("Token variable")} help={T("Environment variable that holds an OAuth access token.")}>
              <TextField value={auth.accessTokenEnv ?? ""} onCommit={(value) => setAuth((next) => (next.accessTokenEnv = optional(value)))} mono />
            </Row>
          )}
          {auth.type === "command" && (
            <Row
              label={T("Command")}
              help={T(
                "Runs in the project folder; what it prints is used: an access token, JSON with access_token, or a service account's JSON key. Works in the CLI and CI too.",
              )}
            >
              <TextField value={auth.command ?? ""} onCommit={(value) => setAuth((next) => (next.command = optional(value)))} mono />
            </Row>
          )}
          {auth.type === "custom" && (
            <>
              <Row label={T("Provider")}>
                <TextField value={auth.provider ?? ""} onCommit={(value) => setAuth((next) => (next.provider = optional(value)))} mono />
              </Row>
              <div className="form-row">
                <span />
                <span className="warning-text">
                  {T("Custom providers are classes in a Unity project and run only in the Unity editor. Here, use \"command\" or build offline from the snapshot.")}
                </span>
              </div>
            </>
          )}
          {folder && auth.type === "public" && (
            <div className="form-row">
              <span />
              <span className="warning-text">
                {T("Google lists a folder only through the Drive API: choose an API key (for a link-shared folder), a service account or an access token.")}
              </span>
            </div>
          )}
          {!folder && (
            <Row label={T("Tabs")} help={T("Optional. Name:gid pairs, e.g. Item:0, Monster:123456. Empty reads every tab.")}>
              <TextField
                value={formatTabs(source.sheets)}
                onCommit={(value) => {
                  const parsed = parseTabs(value);
                  if (parsed === null) {
                    // Keep what was there rather than silently dropping the entries that did not parse.
                    notify(T("Tabs must be Name:gid pairs separated by commas, e.g. Item:0, Monster:123456."));
                    return;
                  }
                  onChange((next) => (next.sheets = parsed.length > 0 ? parsed : undefined));
                }}
                mono
              />
            </Row>
          )}
          <Row
            label={T("Snapshot folder")}
            help={
              folder
                ? T("Optional. Each workbook is saved here as a folder of CSV, for diffs and offline builds.")
                : T("Optional. The fetched CSV is saved here, for diffs and offline builds.")
            }
          >
            <PathField value={source.snapshot ?? ""} browse="folder" onCommit={(value) => onChange((next) => (next.snapshot = optional(value)))} />
          </Row>
        </>
      )}
    </div>
  );
}
