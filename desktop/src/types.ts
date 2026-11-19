// The shapes the engine process (dotnet/HolySheet.Desktop.Host) sends. Keep in step with DesktopProject.cs, TableView.cs, Json.cs.

export type Severity = "error" | "warning";

export interface Diagnostic {
  severity: Severity;
  location: string;
  message: string;
  hint: string;
  /** The diagnostic as one line, the text the copy actions put on the clipboard. */
  text: string;
  workbook?: string;
  sheet?: string;
  cell?: string;
  row?: number;
  column?: number;
}

export type RunStatus = "failed" | "errors" | "checked" | "built" | "upToDate";

export interface RunResult {
  status: RunStatus;
  success: boolean;
  committed: boolean;
  failure: string | null;
  summary: string;
  errors: number;
  warnings: number;
  tables: number;
  rows: number;
  enums: number;
  partial: boolean;
  only: string | null;
  refreshed: string[];
  csvBackend: string;
  readMs: number;
  buildMs: number;
  written: string[];
  deleted: string[];
  unchanged: number;
  finishedAt: string;
  diagnostics: Diagnostic[];
  trigger?: "user" | "watch";
}

export interface AppInfo {
  tool: string;
  runtime: string;
  os: string;
  csvBackends: string[];
  csvDefault: string;
  sepAvailable: boolean;
  sepUnavailableReason: string | null;
  serializers: string[];
}

export type SourceType = "xlsx" | "csv" | "google" | "googleFolder";
export type AuthType = "public" | "apiKey" | "serviceAccount" | "accessToken" | "custom" | "command";

export interface AuthConfig {
  type: AuthType;
  apiKey?: string;
  apiKeyEnv?: string;
  keyFile?: string;
  accessTokenEnv?: string;
  provider?: string;
  command?: string;
}

export interface SheetRef {
  name: string;
  gid: number;
}

export interface SourceConfig {
  type: SourceType;
  path?: string;
  spreadsheetId?: string;
  folderId?: string;
  name?: string;
  auth?: AuthConfig;
  sheets?: SheetRef[];
  snapshot?: string;
}

/** holysheet.json as the engine writes it (HolySheetConfig.ToJson). Optional parts are left out when not set. */
export interface HolySheetConfig {
  sources: SourceConfig[];
  output: { code: string; data: string; csv?: string; schema: boolean; manifestFile?: string };
  codegen: { namespace: string; tablesClass: string; serializer: string };
  sheet: { header: string[]; arraySeparator: string };
  csv?: { backend: string };
  signing?: { publicKey?: string; privateKeyFile?: string; privateKeyEnv?: string };
  build?: { label?: string; parallel?: boolean; cache?: string };
  [key: string]: unknown;
}

export interface SourceState {
  resolved: string | null;
  exists: boolean;
  link: string | null;
  snapshotResolved: string | null;
}

export interface ProjectState {
  dir: string | null;
  configPath: string | null;
  hasConfig: boolean;
  config: HolySheetConfig | null;
  configError: string | null;
  unityProject: boolean;
  sources: SourceState[];
  keyFileExists: boolean;
  keyIgnored: boolean;
  watch: { enabled: boolean; offline: boolean; intervalSec: number; watching: number };
  running: boolean;
  lastRun: RunResult | null;
  /** Only on config.save: what the engine thinks of the saved config. */
  diagnostics?: Diagnostic[];
  privateKeyFile?: string;
}

export type SheetRole = "defines" | "rows" | "columns";

export interface TableInfo {
  name: string;
  kind: "table" | "config";
  location: string;
  workbook: string;
  rows: number;
  columns: number;
  sheets: { workbook: string; sheet: string; role: SheetRole }[];
}

export interface EnumMember {
  name: string;
  value: number;
  /** The value as shown: flags that are one bit read "8  (1 << 3)". */
  display: string;
  comment: string;
}

export interface EnumInfo {
  name: string;
  underlying: string;
  isFlags: boolean;
  comment: string;
  workbook: string;
  sheet: string;
  /** Made by a Mapping column (under its table) rather than an Enums sheet (under its workbook). */
  mapping: boolean;
  members: EnumMember[];
}

export interface WorkbookInfo {
  name: string;
  type: SourceType | null;
  source: string;
  link: string | null;
}

export interface TableModel {
  workbooks: WorkbookInfo[];
  tables: TableInfo[];
  enums: EnumInfo[];
}

export type TypeClass = "" | "int" | "float" | "bool" | "date" | "enum" | "ref" | "mapping";
export type SortKind = "text" | "number" | "bool";

export interface PreviewColumn {
  name: string;
  type: string;
  comment: string;
  sheet: string;
  letter: string;
  typeClass: TypeClass;
  isArray: boolean;
  sortKind: SortKind;
  number: boolean;
  key: boolean;
}

export interface PreviewRow {
  label: string;
  sheet: string;
  row: number;
  cells: string[];
}

export interface Preview {
  table: string;
  sheet: string | null;
  kind: "table" | "config";
  workbook: string;
  location: string;
  definingSheet: string;
  keyColumn: number;
  columns: PreviewColumn[];
  rows: PreviewRow[];
}

export interface Located {
  target: string | null;
  isUrl: boolean;
  isFolder: boolean;
}

export interface VerifyReport {
  success: boolean;
  directory: string;
  signature: string;
  tables: number;
  content: string | null;
  problems: string[];
}
