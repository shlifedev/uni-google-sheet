import { useEffect, useMemo, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { useApp } from "../store";
import type { SortSpec } from "../lib/sort";
import type { TypeClass } from "../types";
import { Highlight } from "./Highlight";
import { KeyIcon } from "./Icons";

export interface GridColumn {
  id: string;
  title: string;
  type?: string;
  typeClass?: TypeClass;
  comment?: string;
  number: boolean;
  key?: boolean;
  width: number;
}

interface Props<T> {
  identity: string;
  columns: GridColumn[];
  rows: T[];
  cell: (row: T, column: number) => string;
  cellTitle?: (row: T, column: number) => string;
  rowHead?: { title: string; label: (row: T) => string; tooltip: (row: T) => string; width: number };
  sorts: SortSpec[];
  onSort: (sorts: SortSpec[]) => void;
  highlight?: { query: string; columns: Set<number> | null };
}

const rowHeight = 20;

export function nextSorts(sorts: SortSpec[], column: number | "row", additive: boolean): SortSpec[] {
  const existing = sorts.find((sort) => sort.column === column);
  if (additive) {
    if (!existing) return [...sorts, { column, descending: false }];
    return sorts.map((sort) => (sort.column === column ? { ...sort, descending: !sort.descending } : sort));
  }
  if (sorts.length === 1 && existing) {
    return existing.descending ? [] : [{ column, descending: true }];
  }
  return [{ column, descending: false }];
}

export function DataGrid<T>({ identity, columns, rows, cell, cellTitle, rowHead, sorts, onSort, highlight }: Props<T>) {
  const { preferences } = useApp();
  const scroller = useRef<HTMLDivElement>(null);
  const [widths, setWidths] = useState<Record<string, number>>({});

  useEffect(() => {
    setWidths({});
    scroller.current?.scrollTo({ top: 0, left: 0 });
  }, [identity]);

  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scroller.current,
    estimateSize: () => rowHeight,
    overscan: 12,
  });

  const columnWidths = columns.map((column) => widths[column.id] ?? column.width);
  const totalWidth = (rowHead?.width ?? 0) + columnWidths.reduce((sum, width) => sum + width, 0);

  const startResize = (event: React.PointerEvent, column: GridColumn, start: number) => {
    event.preventDefault();
    event.stopPropagation();
    const origin = event.clientX;
    const move = (moved: PointerEvent) => setWidths((current) => ({ ...current, [column.id]: Math.max(40, start + moved.clientX - origin) }));
    const up = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
  };

  const sortMark = (column: number | "row") => {
    const index = sorts.findIndex((sort) => sort.column === column);
    if (index < 0) return null;
    return (
      <span className="sort">
        {sorts[index].descending ? "▼" : "▲"}
        {sorts.length > 1 ? index + 1 : ""}
      </span>
    );
  };

  const typed = columns.some((column) => column.type);
  const alignment = preferences.alignment;
  const className = useMemo(
    () => ["grid", `lines-${preferences.gridLines}`, preferences.stripes ? "stripes" : "", alignment !== "auto" ? `align-${alignment}` : ""].join(" "),
    [preferences.gridLines, preferences.stripes, alignment],
  );

  return (
    <div ref={scroller} className={className} tabIndex={-1}>
      <div className="grid-header" style={{ width: totalWidth }}>
        {rowHead && (
          <div
            className="header-cell rowhead"
            style={{ width: rowHead.width }}
            onClick={(event) => onSort(nextSorts(sorts, "row", event.shiftKey))}
          >
            <div className="name">
              {rowHead.title}
              {sortMark("row")}
            </div>
            {typed && <div className="type">&nbsp;</div>}
          </div>
        )}
        {columns.map((column, index) => (
          <div
            key={column.id}
            className="header-cell"
            style={{ width: columnWidths[index] }}
            title={[column.title, column.type, column.comment].filter(Boolean).join("\n")}
            onClick={(event) => onSort(nextSorts(sorts, index, event.shiftKey))}
          >
            <div className="name">
              {column.key && <KeyIcon className="faint" />}
              <span className="ellipsis">{column.title}</span>
              {sortMark(index)}
            </div>
            {typed && <div className={`type ${column.typeClass ? "type-" + column.typeClass : ""}`}>{column.type || " "}</div>}
            <div className="resize" onPointerDown={(event) => startResize(event, column, columnWidths[index])} onClick={(event) => event.stopPropagation()} />
          </div>
        ))}
      </div>
      <div className="grid-inner" style={{ height: virtualizer.getTotalSize(), width: totalWidth }}>
        {virtualizer.getVirtualItems().map((item) => {
          const row = rows[item.index];
          return (
            <div key={item.key} className={`grid-row ${item.index % 2 === 1 ? "odd" : ""}`} style={{ top: item.start, width: totalWidth }}>
              {rowHead && (
                <div className="cell rowhead" style={{ width: rowHead.width }} title={rowHead.tooltip(row)}>
                  {rowHead.label(row)}
                </div>
              )}
              {columns.map((column, index) => {
                const text = cell(row, index).replace(/\r?\n/g, " ");
                const marked = highlight && highlight.query && (highlight.columns === null || highlight.columns.has(index));
                return (
                  <div
                    key={column.id}
                    className={`cell ${column.number && alignment === "auto" ? "number" : ""}`}
                    style={{ width: columnWidths[index] }}
                    title={cellTitle?.(row, index)}
                  >
                    {marked ? <Highlight text={text} query={highlight!.query} /> : text}
                  </div>
                );
              })}
            </div>
          );
        })}
      </div>
    </div>
  );
}
