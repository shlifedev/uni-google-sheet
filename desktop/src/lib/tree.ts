// The table tree, built the way the Unity window builds it (HolySheetWindow.FillTree): one node per connected source once
// there is more than one, then workbooks, their tables (with the sheets a table was built from), and enums: those an
// Enums sheet defines under their workbook, those a Mapping column makes under its table.
import { Count, T, F } from "../i18n";
import type { EnumInfo, SheetRole, SourceType, TableInfo, TableModel, WorkbookInfo } from "../types";

export type NodeKind = "group" | "workbook" | "table" | "config" | "sheet" | "enum" | "enumsheet";
export type IconName = "excel" | "sheets" | "drive" | "folder" | "table" | "config" | "enum";

export interface TreeNode {
  /** Identifies the node across rebuilds, so the selection and the open nodes survive a run. */
  key: string;
  kind: NodeKind;
  label: string;
  icon: IconName;
  note: string;
  tooltip: string;
  children: TreeNode[];
  table?: TableInfo;
  /** On sheet, enum and enum sheet nodes. */
  sheet?: string;
  /** On workbook, sheet, enum and enum sheet nodes. */
  workbook?: string;
  /** On group nodes: the workbooks under it. */
  groupWorkbooks?: string[];
  /** On enum nodes: the enum; on enum sheet nodes: every enum the sheet defines. */
  enums?: EnumInfo[];
}

type Group = "excel" | "googleSheets" | "googleDrive" | "csv" | "other";
const groupOrder: Group[] = ["excel", "googleSheets", "googleDrive", "csv", "other"];

export function groupLabel(group: Group): string {
  switch (group) {
    case "excel":
      return "Excel";
    case "googleSheets":
      return "Google Sheets";
    case "googleDrive":
      return "Google Drive";
    case "csv":
      return "CSV";
    default:
      return T("Other");
  }
}

function groupOf(type: SourceType | null | undefined, workbook: string): Group {
  switch (type) {
    case "xlsx":
      return "excel";
    case "csv":
      return "csv";
    case "google":
      return "googleSheets";
    case "googleFolder":
      return "googleDrive";
    default:
      return workbook.toLowerCase().endsWith(".xlsx") ? "excel" : "other";
  }
}

function groupIcon(group: Group): IconName {
  switch (group) {
    case "excel":
      return "excel";
    case "googleSheets":
      return "sheets";
    case "googleDrive":
      return "drive";
    default:
      return "folder";
  }
}

// xlsx files show as Excel workbooks, CSV folders as folders, Google spreadsheets as Sheets. A Drive folder holds both.
function workbookIcon(origin: WorkbookInfo | undefined, workbook: string): IconName {
  const isXlsx = workbook.toLowerCase().endsWith(".xlsx");
  switch (origin?.type) {
    case "csv":
      return "folder";
    case "google":
      return "sheets";
    case "googleFolder":
      return isXlsx ? "excel" : "sheets";
    case "xlsx":
      return "excel";
    default:
      return isXlsx ? "excel" : "sheets";
  }
}

// The last folder of a path, or the start of a Google id.
export function shortSource(source: string): string {
  const trimmed = source.replace(/[/\\]+$/, "");
  const slash = Math.max(trimmed.lastIndexOf("/"), trimmed.lastIndexOf("\\"));
  if (slash >= 0) return trimmed.slice(slash + 1);
  return trimmed.length > 10 ? trimmed.slice(0, 8) + "…" : trimmed;
}

/** The sheet rule: "Enums", or a name ending in ".Enums" (Battle.Enums). */
export function isEnumsSheet(sheet: string): boolean {
  return sheet === "Enums" || sheet.endsWith(".Enums");
}

export function describeSheet(table: string, role: SheetRole): string {
  switch (role) {
    case "columns":
      return F("Adds columns to {0}, matched by key.", table);
    case "defines":
      return F("Defines {0}.", table);
    default:
      return F("Adds rows to {0}.", table);
  }
}

/** "byte · Flags · 6 members" / "int · Mapping · 12 members". */
export function describeEnum(definition: EnumInfo): string {
  return (
    definition.underlying +
    (definition.isFlags ? " · Flags" : "") +
    (definition.mapping ? " · Mapping" : "") +
    " · " +
    Count(definition.members.length, "member")
  );
}

function enumNode(definition: EnumInfo): TreeNode {
  return {
    key: "enum:" + definition.name,
    kind: "enum",
    label: definition.name,
    icon: "enum",
    note: String(definition.members.length),
    tooltip: describeEnum(definition) + (definition.comment ? "\n" + definition.comment : ""),
    children: [],
    workbook: definition.workbook,
    sheet: definition.sheet,
    enums: [definition],
  };
}

const byName = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);
const byNameIgnoreCase = (a: string, b: string) => byName(a.toLowerCase(), b.toLowerCase());

export function buildTree(model: TableModel): TreeNode[] {
  const origins = new Map(model.workbooks.map((workbook) => [workbook.name, workbook]));
  const connection = (workbook: string) => {
    const origin = origins.get(workbook);
    return { group: groupOf(origin?.type, workbook), source: origin?.source ?? "" };
  };

  const sheetEnums = new Map<string, { sheet: string; enums: EnumInfo[] }[]>();
  const mappingEnums = new Map<string, EnumInfo[]>();
  for (const definition of model.enums) {
    if (!definition.mapping) {
      const sheets = sheetEnums.get(definition.workbook) ?? [];
      sheetEnums.set(definition.workbook, sheets);
      let entry = sheets.find((candidate) => candidate.sheet === definition.sheet);
      if (!entry) {
        entry = { sheet: definition.sheet, enums: [] };
        sheets.push(entry);
      }
      entry.enums.push(definition);
    } else {
      const at = definition.workbook + " / " + definition.sheet;
      mappingEnums.set(at, [...(mappingEnums.get(at) ?? []), definition]);
    }
  }

  const tablesByWorkbook = new Map<string, TableInfo[]>();
  for (const table of model.tables) {
    tablesByWorkbook.set(table.workbook, [...(tablesByWorkbook.get(table.workbook) ?? []), table]);
  }

  const workbooks = [...new Set([...tablesByWorkbook.keys(), ...sheetEnums.keys()])].sort((a, b) => {
    const ca = connection(a);
    const cb = connection(b);
    return (
      groupOrder.indexOf(ca.group) - groupOrder.indexOf(cb.group) ||
      byNameIgnoreCase(ca.source, cb.source) ||
      byNameIgnoreCase(a, b)
    );
  });

  const workbookNodes: { group: Group; source: string; workbook: string; node: TreeNode }[] = [];
  for (const workbook of workbooks) {
    const children: TreeNode[] = [];
    const tables = [...(tablesByWorkbook.get(workbook) ?? [])].sort((a, b) => byName(a.name, b.name));
    for (const table of tables) {
      const sheetNodes: TreeNode[] = [];
      if (table.sheets.length > 1) {
        for (const entry of table.sheets) {
          sheetNodes.push({
            key: `sheet:${table.name}/${entry.sheet}`,
            kind: "sheet",
            label: entry.sheet,
            icon: "table",
            note: "",
            tooltip: describeSheet(table.name, entry.role),
            children: [],
            table,
            sheet: entry.sheet,
            workbook: entry.workbook,
          });
        }
      }

      // Enums a Mapping column of this table makes.
      for (const entry of table.sheets) {
        for (const definition of mappingEnums.get(entry.workbook + " / " + entry.sheet) ?? []) {
          sheetNodes.push(enumNode(definition));
        }
      }

      const isConfig = table.kind === "config";
      children.push({
        key: "table:" + table.name,
        kind: isConfig ? "config" : "table",
        label: table.name,
        icon: isConfig ? "config" : "table",
        // A table built from more than one sheet (Item + Item.Rare + Item.Col.Balance) says how many more.
        note: table.sheets.length > 1 ? "+" + (table.sheets.length - 1) : "",
        tooltip:
          (isConfig ? T("Config table: one row, fields written down the sheet.\n") : "") +
          Count(table.rows, "row") +
          " · " +
          Count(table.columns, "column") +
          "\n" +
          table.sheets.map((entry) => entry.sheet).join("\n"),
        children: sheetNodes,
        table,
      });
    }

    // Enums sheets, after the tables.
    for (const { sheet, enums } of sheetEnums.get(workbook) ?? []) {
      children.push({
        key: `enumsheet:${workbook}/${sheet}`,
        kind: "enumsheet",
        label: sheet,
        icon: "enum",
        note: String(enums.length),
        tooltip: Count(enums.length, "enum") + "\n" + enums.map((definition) => definition.name).join("\n"),
        children: enums.map(enumNode),
        workbook,
        sheet,
        enums,
      });
    }

    if (children.length > 0) {
      const origin = origins.get(workbook);
      const { group, source } = connection(workbook);
      workbookNodes.push({
        group,
        source,
        workbook,
        node: {
          key: "workbook:" + workbook,
          kind: "workbook",
          label: workbook,
          icon: workbookIcon(origin, workbook),
          note: "",
          tooltip: origin && origin.source ? `${workbook}\n${groupLabel(group)}: ${origin.source}` : workbook,
          children,
          workbook,
        },
      });
    }
  }

  // One node per connected source (each entry under "sources" in holysheet.json), once there is more than one.
  const connections = new Map<string, { group: Group; source: string; entries: typeof workbookNodes }>();
  for (const entry of workbookNodes) {
    const id = entry.group + "\u0000" + entry.source;
    const found = connections.get(id) ?? { group: entry.group, source: entry.source, entries: [] };
    found.entries.push(entry);
    connections.set(id, found);
  }

  if (connections.size <= 1) {
    return workbookNodes.map((entry) => entry.node);
  }

  // A kind with more than one connection (two Excel folders) is told apart by its folder or id.
  const perKind = new Map<Group, number>();
  for (const { group } of connections.values()) perKind.set(group, (perKind.get(group) ?? 0) + 1);

  return [...connections.values()].map(({ group, source, entries }) => {
    const names = entries.map((entry) => entry.workbook);
    let label = groupLabel(group);
    if ((perKind.get(group) ?? 0) > 1 && source.length > 0) label += " · " + shortSource(source);
    return {
      key: `group:${group}:${source}`,
      kind: "group" as const,
      label,
      icon: groupIcon(group),
      note: String(names.length),
      tooltip: (source.length > 0 ? `${groupLabel(group)}: ${source}\n` : "") + Count(names.length, "workbook") + "\n" + names.join("\n"),
      children: entries.map((entry) => entry.node),
      groupWorkbooks: names,
    };
  });
}

/** Every node, depth first, with its parent keys: for search, selection lookups and "expand to show". */
export function flattenTree(nodes: TreeNode[], parents: string[] = [], out: { node: TreeNode; parents: string[] }[] = []) {
  for (const node of nodes) {
    out.push({ node, parents });
    flattenTree(node.children, [...parents, node.key], out);
  }
  return out;
}

/** The keys open by default: groups, workbooks. */
export function defaultExpanded(nodes: TreeNode[]): Set<string> {
  const keys = new Set<string>();
  for (const { node } of flattenTree(nodes)) {
    if (node.kind === "group" || node.kind === "workbook") keys.add(node.key);
  }
  return keys;
}

/** What a set of selected nodes reads again in a partial build: whole workbooks, and single sheets. */
export interface Selection {
  workbooks: Set<string>;
  sheets: Map<string, { workbook: string; sheet: string }>;
}

export function selectionOf(nodes: TreeNode[]): Selection {
  const selection: Selection = { workbooks: new Set(), sheets: new Map() };
  const addSheet = (workbook: string, sheet: string) => selection.sheets.set(workbook + "\u0000" + sheet, { workbook, sheet });
  for (const node of nodes) {
    if (node.groupWorkbooks) {
      node.groupWorkbooks.forEach((workbook) => selection.workbooks.add(workbook));
    } else if (node.kind === "workbook") {
      selection.workbooks.add(node.workbook!);
    } else if (node.sheet !== undefined && node.workbook !== undefined) {
      addSheet(node.workbook, node.sheet);
    } else if (node.table) {
      node.table.sheets.forEach((entry) => addSheet(entry.workbook, entry.sheet));
    }
  }
  return selection;
}

export function isSelectionEmpty(selection: Selection): boolean {
  return selection.workbooks.size === 0 && selection.sheets.size === 0;
}

export function includes(selection: Selection, workbook: string, sheet: string): boolean {
  return selection.workbooks.has(workbook) || selection.sheets.has(workbook + "\u0000" + sheet);
}

/** The --only entries for the engine: "Item.xlsx" or "Item.xlsx/Item.Rare". */
export function onlyArguments(selection: Selection): string[] {
  return [
    ...[...selection.workbooks].sort(byName),
    ...[...selection.sheets.values()].filter((entry) => !selection.workbooks.has(entry.workbook)).map((entry) => `${entry.workbook}/${entry.sheet}`),
  ];
}

/** The tables a selection builds again, and the enums of Enums sheets it reads again. */
export function selectionImpact(model: TableModel, selection: Selection): { tables: string[]; enums: string[] } {
  const tables = model.tables
    .filter((table) => table.sheets.some((entry) => includes(selection, entry.workbook, entry.sheet)))
    .map((table) => table.name)
    .sort(byName);
  const enums = model.enums
    .filter((definition) => !definition.mapping && includes(selection, definition.workbook, definition.sheet))
    .map((definition) => definition.name);
  return { tables, enums };
}

/** "Item.xlsx", "Job · 2 sheets", "3 sheets", "Item.xlsx + 2 sheets": what the selection bar calls the selection. */
export function describeSelection(selected: TreeNode[], selection: Selection): string {
  if (selected.length === 1) {
    const node = selected[0];
    if (node.groupWorkbooks) return node.label + " · " + Count(node.groupWorkbooks.length, "workbook");
    const count = selection.sheets.size;
    return node.label + (selection.workbooks.size === 0 && count > 1 ? " · " + Count(count, "sheet") : "");
  }
  const workbooks = selection.workbooks.size;
  const sheets = selection.sheets.size;
  const workbookText = workbooks === 1 ? [...selection.workbooks][0] : Count(workbooks, "workbook");
  const sheetText = sheets === 1 ? [...selection.sheets.values()][0].sheet : Count(sheets, "sheet");
  return workbooks === 0 ? sheetText : sheets === 0 ? workbookText : workbookText + " + " + sheetText;
}
