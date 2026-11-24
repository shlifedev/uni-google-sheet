import { useEffect, useMemo, useRef, useState } from "react";
import { rpc } from "../rpc";
import { useApp } from "../store";
import { useTablesView } from "../tablesView";
import { Count, F, T, formatNumber } from "../i18n";
import { containsText, sortRows, type SortSpec } from "../lib/sort";
import { describeEnum, isEnumsSheet, type TreeNode } from "../lib/tree";
import { shortcut } from "../lib/actions";
import type { EnumInfo, EnumMember, Preview as PreviewData, PreviewRow } from "../types";
import { DataGrid, type GridColumn } from "./DataGrid";
import { showMenuBelow, type MenuItem } from "./Menu";
import { CloseIcon, SearchIcon } from "./Icons";
import type { Alignment, GridLines } from "../lib/settings";

function useViewMenu(): (element: HTMLElement) => void {
  const { preferences, setPreferences } = useApp();
  return (element) => {
    const lines: [GridLines, string][] = [
      ["none", T("None")],
      ["rows", T("Rows")],
      ["columns", T("Columns")],
      ["both", T("Both")],
    ];
    const alignments: [Alignment, string][] = [
      ["auto", T("Auto")],
      ["left", T("Left")],
      ["center", T("Center")],
      ["right", T("Right")],
    ];
    const items: MenuItem[] = [
      { heading: T("Grid lines") },
      ...lines.map(([value, label]) => ({ label, checked: preferences.gridLines === value, action: () => setPreferences({ gridLines: value }) })),
      { separator: true },
      { heading: T("Alignment") },
      ...alignments.map(([value, label]) => ({ label, checked: preferences.alignment === value, action: () => setPreferences({ alignment: value }) })),
      { separator: true },
      { label: T("Striped rows"), checked: preferences.stripes, action: () => setPreferences({ stripes: !preferences.stripes }) },
    ];
    showMenuBelow(element, items);
  };
}

export function PreviewPane({ node }: { node: TreeNode | null }) {
  if (!node) return <div className="view"><div className="empty">{T("Select a table to see its rows.")}</div></div>;
  if (node.enums) return <EnumPreview node={node} />;
  if (node.table) return <TablePreview table={node.table.name} sheet={node.sheet ?? null} />;
  const workbooks = node.groupWorkbooks ?? (node.workbook ? [node.workbook] : []);
  return (
    <div className="view">
      <div className="empty">
        <strong style={{ color: "var(--text)" }}>{node.label}</strong>
        <span className="tooltip-lines">{node.tooltip}</span>
        <span>{workbooks.length > 0 && T("Select tables or sheets on the left to build only those.")}</span>
      </div>
    </div>
  );
}

function TablePreview({ table, sheet }: { table: string; sheet: string | null }) {
  const { revision, preferences, setPreferences, notify } = useApp();
  const { findOpen, findQuery, setFind } = useTablesView();
  const [data, setData] = useState<PreviewData | null>(null);
  const [sorts, setSorts] = useState<SortSpec[]>([]);
  const findRef = useRef<HTMLInputElement>(null);
  const viewMenu = useViewMenu();

  useEffect(() => {
    let current = true;
    rpc
      .preview(table, sheet)
      .then((preview) => current && setData(preview))
      .catch((error) => {
        if (current) {
          setData(null);
          notify(error instanceof Error ? error.message : String(error));
        }
      });
    return () => {
      current = false;
    };
  }, [table, sheet, revision, notify]);

  useEffect(() => setSorts([]), [table, sheet]);

  useEffect(() => {
    if (findOpen) findRef.current?.focus();
  }, [findOpen]);

  const keyColumn = data?.keyColumn ?? -1;
  const hasKey = keyColumn >= 0;
  const searchAll = preferences.findInAllColumns || !hasKey;
  const query = findOpen ? findQuery.trim() : "";

  const rows = useMemo(() => {
    if (!data) return [] as PreviewRow[];
    let shown = data.rows;
    if (query) {
      shown = shown.filter((row) =>
        searchAll ? row.cells.some((text) => containsText(text, query)) : containsText(row.cells[keyColumn] ?? "", query),
      );
    }
    return sortRows(shown, sorts, (column) => {
      const info = data.columns[column];
      return info ? { kind: info.sortKind, text: (row: PreviewRow) => row.cells[column] ?? "" } : null;
    });
  }, [data, query, searchAll, keyColumn, sorts]);

  if (!data || data.table !== table || data.sheet !== sheet) {
    return <div className="view"><div className="empty">{T("Loading…")}</div></div>;
  }

  const columns: GridColumn[] = data.columns.map((column, index) => ({
    id: `${index}:${column.name}`,
    title: column.name,
    type: column.type,
    typeClass: column.typeClass,
    comment: column.comment,
    number: column.number,
    key: column.key,
    width: 120,
  }));
  const labelLength = data.rows.reduce((longest, row) => Math.max(longest, row.label.length), 1);
  const location = sheet ? F("{0}  ·  part of {1}", data.location, data.table) : data.location;

  return (
    <div className="view">
      <div className="caption">
        <span className="title ellipsis">{location}</span>
        <span className="faint" style={{ whiteSpace: "nowrap" }}>
          {Count(data.rows.length, "row")} · {Count(data.columns.length, "column")}
        </span>
        <div className="fill" />
        <span className="faint" style={{ fontSize: 11, whiteSpace: "nowrap" }}>
          {F("{0} to find", shortcut("F"))}
        </span>
        <button className="button small ghost" onClick={(event) => viewMenu(event.currentTarget)}>
          {T("View")}
        </button>
      </div>
      {findOpen && (
        <div className="find-bar">
          <div className="segmented">
            <button
              className={!searchAll ? "on" : ""}
              disabled={!hasKey}
              title={hasKey ? F("Search {0}", data.columns[keyColumn].name) : T("This table has no key")}
              onClick={() => setPreferences({ findInAllColumns: false })}
            >
              {T("Key")}
            </button>
            <button className={searchAll ? "on" : ""} title={T("Search every column")} onClick={() => setPreferences({ findInAllColumns: true })}>
              {T("All")}
            </button>
          </div>
          <div className="search">
            <SearchIcon />
            <input
              ref={findRef}
              className="field"
              value={findQuery}
              title={T("Rows whose cell contains this text. Esc closes.")}
              onChange={(event) => setFind(true, event.target.value)}
              onKeyDown={(event) => event.key === "Escape" && setFind(false)}
            />
          </div>
          {query && (
            <span className="faint">{F("{0} of {1}", formatNumber(rows.length), formatNumber(data.rows.length))}</span>
          )}
          <div className="fill" />
          <button className="icon-button" title={T("Close")} onClick={() => setFind(false)}>
            <CloseIcon />
          </button>
        </div>
      )}
      {query && rows.length === 0 ? (
        <div className="empty">{T("No rows match.")}</div>
      ) : (
        <DataGrid
          identity={`${data.table}/${data.sheet ?? ""}`}
          columns={columns}
          rows={rows}
          cell={(row, column) => row.cells[column] ?? ""}
          cellTitle={(row, column) => {
            const info = data.columns[column];
            return info.sheet === row.sheet ? `${row.sheet}!${info.letter}${row.row}` : info.sheet;
          }}
          rowHead={{
            title: T("Row"),
            label: (row) => row.label,
            tooltip: (row) => F("{0} row {1}", row.sheet, row.row),
            width: Math.max(40, 16 + 6.5 * labelLength),
          }}
          sorts={sorts}
          onSort={setSorts}
          highlight={query ? { query, columns: searchAll ? null : new Set([keyColumn]) } : undefined}
        />
      )}
    </div>
  );
}

function EnumPreview({ node }: { node: TreeNode }) {
  const definitions = node.enums ?? [];
  const several = node.kind === "enumsheet";
  const [sorts, setSorts] = useState<SortSpec[]>([]);
  const viewMenu = useViewMenu();
  useEffect(() => setSorts([]), [node.key]);

  const rows = useMemo(() => {
    const all: { definition: EnumInfo; member: EnumMember }[] = [];
    for (const definition of definitions) for (const member of definition.members) all.push({ definition, member });
    return all;
  }, [definitions]);

  const fields: { title: string; width: number; number: boolean; text: (row: (typeof rows)[number]) => string }[] = [
    ...(several ? [{ title: T("Enum"), width: 160, number: false, text: (row: (typeof rows)[number]) => row.definition.name }] : []),
    { title: T("Member"), width: 180, number: false, text: (row) => row.member.name },
    { title: T("Value"), width: 120, number: true, text: (row) => row.member.display },
    { title: T("Comment"), width: 320, number: false, text: (row) => row.member.comment },
  ];

  const sorted = useMemo(
    () => sortRows(rows, sorts, (column) => (fields[column] ? { kind: fields[column].number ? "number" : "text", text: fields[column].text } : null)),
    [rows, sorts], // eslint-disable-line react-hooks/exhaustive-deps
  );

  const where = `${node.workbook ?? ""} / ${node.sheet ?? ""}`;
  const single = definitions[0];
  const mapping = !several && single && !isEnumsSheet(node.sheet ?? "");
  return (
    <div className="view">
      <div className="caption">
        <span className="title ellipsis">
          {several ? `${where}  ·  ${Count(definitions.length, "enum")}` : `${where}  ·  ${single?.name}  ·  ${single ? describeEnum({ ...single, mapping: !!mapping }) : ""}`}
        </span>
        <div className="fill" />
        <button className="button small ghost" onClick={(event) => viewMenu(event.currentTarget)}>
          {T("View")}
        </button>
      </div>
      {!several && single?.comment && <div className="muted" style={{ padding: "6px 10px", borderBottom: "1px solid var(--border)" }}>{single.comment}</div>}
      <DataGrid
        identity={node.key}
        columns={fields.map((field, index) => ({ id: String(index), title: field.title, number: field.number, width: field.width }))}
        rows={sorted}
        cell={(row, column) => fields[column].text(row)}
        sorts={sorts}
        onSort={setSorts}
      />
    </div>
  );
}
