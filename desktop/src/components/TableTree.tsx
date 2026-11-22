import { useEffect, useMemo, useRef } from "react";
import { useApp } from "../store";
import { useTablesView } from "../tablesView";
import { useSelectionBuild } from "../selection";
import { Count, F, T } from "../i18n";
import { containsText } from "../lib/sort";
import {
  defaultExpanded,
  describeSelection,
  flattenTree,
  isSelectionEmpty,
  onlyArguments,
  selectionOf,
  type TreeNode,
} from "../lib/tree";
import { copy, openSource, reveal } from "../lib/actions";
import { showMenu } from "./Menu";
import { Chevron, NodeIcon, SearchIcon } from "./Icons";
import { Highlight } from "./Highlight";

/** Where a node opens: a workbook, at a sheet when the node has one. */
export function sourceOf(node: TreeNode): { workbook: string; sheet: string | null } | null {
  if (node.kind === "group") return null;
  if (node.table && node.sheet === undefined) return { workbook: node.table.workbook, sheet: node.table.sheets[0]?.sheet ?? null };
  if (node.workbook) return { workbook: node.workbook, sheet: node.sheet ?? null };
  return null;
}

export function TableTree({ tree }: { tree: TreeNode[] }) {
  const { model, running, project, startRun } = useApp();
  const view = useTablesView();
  const listRef = useRef<HTMLDivElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);

  const all = useMemo(() => flattenTree(tree), [tree]);
  const byKey = useMemo(() => new Map(all.map((entry) => [entry.node.key, entry])), [all]);
  const expanded = view.expanded ?? defaultExpanded(tree);

  // Keep the selection across runs; when nothing selected is left, select the first table, as the window does.
  useEffect(() => {
    const kept = view.selected.filter((key) => byKey.has(key));
    if (kept.length === view.selected.length && (kept.length > 0 || all.length === 0)) return;
    if (kept.length > 0) {
      view.select(kept, kept.includes(view.focus ?? "") ? view.focus : kept[0]);
      return;
    }
    const first = all.find((entry) => entry.node.table);
    if (first) {
      view.select([first.node.key], first.node.key, first.node.key);
      if (first.parents.some((key) => !expanded.has(key))) view.setExpanded(new Set([...expanded, ...first.parents]));
    }
  }, [byKey]); // eslint-disable-line react-hooks/exhaustive-deps

  // Search marks matching names and opens the nodes above them; it does not hide anything.
  const query = view.search.trim();
  const matches = useMemo(() => {
    if (!query) return [] as string[];
    const found = all.filter((entry) => containsText(entry.node.label, query));
    // Exact names first, then tree order.
    const exact = found.filter((entry) => entry.node.label.toLowerCase() === query.toLowerCase());
    return [...exact, ...found.filter((entry) => !exact.includes(entry))].map((entry) => entry.node.key);
  }, [all, query]);

  const shownExpanded = useMemo(() => {
    if (matches.length === 0) return expanded;
    const open = new Set(expanded);
    for (const key of matches) byKey.get(key)?.parents.forEach((parent) => open.add(parent));
    return open;
  }, [expanded, matches, byKey]);

  const visible = useMemo(() => {
    const rows: { node: TreeNode; depth: number }[] = [];
    const walk = (nodes: TreeNode[], depth: number) => {
      for (const node of nodes) {
        rows.push({ node, depth });
        if (node.children.length > 0 && shownExpanded.has(node.key)) walk(node.children, depth + 1);
      }
    };
    walk(tree, 0);
    return rows;
  }, [tree, shownExpanded]);

  const selectedNodes = view.selected.map((key) => byKey.get(key)?.node).filter((node): node is TreeNode => !!node);
  const build = useSelectionBuild();

  const scrollTo = (key: string) => {
    requestAnimationFrame(() => listRef.current?.querySelector(`[data-key="${CSS.escape(key)}"]`)?.scrollIntoView({ block: "nearest" }));
  };

  const click = (event: React.MouseEvent, node: TreeNode) => {
    if (event.shiftKey && view.anchor) {
      const keys = visible.map((row) => row.node.key);
      const from = keys.indexOf(view.anchor);
      const to = keys.indexOf(node.key);
      if (from >= 0 && to >= 0) {
        const range = keys.slice(Math.min(from, to), Math.max(from, to) + 1);
        view.select(range, node.key, view.anchor);
        return;
      }
    }
    if (event.metaKey || event.ctrlKey) {
      const has = view.selected.includes(node.key);
      const next = has ? view.selected.filter((key) => key !== node.key) : [...view.selected, node.key];
      view.select(next, has ? (next[next.length - 1] ?? null) : node.key, node.key);
      return;
    }
    view.select([node.key], node.key, node.key);
  };

  const keyDown = (event: React.KeyboardEvent) => {
    const keys = visible.map((row) => row.node.key);
    const at = keys.indexOf(view.focus ?? "");
    const focusNode = byKey.get(view.focus ?? "")?.node;
    const moveTo = (index: number) => {
      const key = keys[Math.max(0, Math.min(keys.length - 1, index))];
      if (!key) return;
      view.select([key], key, key);
      scrollTo(key);
    };
    switch (event.key) {
      case "ArrowDown":
        event.preventDefault();
        moveTo(at + 1);
        break;
      case "ArrowUp":
        event.preventDefault();
        moveTo(at - 1);
        break;
      case "ArrowRight":
        if (focusNode && focusNode.children.length > 0) {
          event.preventDefault();
          if (!shownExpanded.has(focusNode.key)) view.setExpanded(new Set([...expanded, focusNode.key]));
          else moveTo(at + 1);
        }
        break;
      case "ArrowLeft":
        if (focusNode) {
          event.preventDefault();
          if (focusNode.children.length > 0 && shownExpanded.has(focusNode.key)) {
            const next = new Set(expanded);
            next.delete(focusNode.key);
            view.setExpanded(next);
          } else {
            const parent = byKey.get(focusNode.key)?.parents.at(-1);
            if (parent) moveTo(keys.indexOf(parent));
          }
        }
        break;
      case "Enter":
        if (focusNode) {
          const source = sourceOf(focusNode);
          if (source) void openSource(source.workbook, source.sheet);
        }
        break;
    }
  };

  const stepSearch = (step: number) => {
    if (matches.length === 0) return;
    const index = (view.searchIndex + step + matches.length) % matches.length;
    view.setSearchIndex(index);
    const key = matches[index];
    view.select([key], key, key);
    scrollTo(key);
  };

  const nodeMenu = (event: React.MouseEvent, node: TreeNode) => {
    // Right-clicking outside the selection selects the node first, as file browsers do.
    let nodes = selectedNodes;
    if (!view.selected.includes(node.key)) {
      view.select([node.key], node.key, node.key);
      nodes = [node];
    }
    const target = selectionOf(nodes);
    const label = describeSelection(nodes, target);
    const source = sourceOf(node);
    const runnable = !!project?.config && !running && !isSelectionEmpty(target);
    const tab = nodes.length === 1 ? node.googleTab : undefined;
    const changeCopy = (useLocal: boolean) => tab && void startRun(false, [], { copy: { workbook: tab.workbook, sheet: tab.sheet, useLocal } });
    showMenu(event, [
      { label: F("Build {0}", label), disabled: !runnable, action: () => void startRun(true, onlyArguments(target)) },
      { label: F("Check {0}", label), disabled: !runnable, action: () => void startRun(false, onlyArguments(target)) },
      { separator: true },
      ...(tab ? [
        { label: T(tab.localCopyUtc ? "Download again from Google" : "Use local copy"), disabled: !runnable, action: () => changeCopy(true) },
        ...(tab.localPath ? [
          { label: T("Show local CSV in folder"), action: () => void reveal(tab.localPath!) },
          { label: T("Use Google original"), disabled: !runnable, action: () => changeCopy(false) },
        ] : []),
        { separator: true as const },
      ] : []),
      { label: T("Open"), disabled: !source, action: () => source && void openSource(source.workbook, source.sheet) },
      { label: T("Copy name"), action: () => void copy(node.label) },
    ]);
  };

  return (
    <div className="sidebar">
      <div className="sidebar-head">
        <div className="search fill">
          <SearchIcon />
          <input
            ref={searchRef}
            className="field"
            placeholder={T("Search tables")}
            value={view.search}
            onChange={(event) => view.setSearch(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === "Enter") stepSearch(event.shiftKey ? -1 : 1);
              if (event.key === "Escape") view.setSearch("");
            }}
          />
        </div>
        {query && (
          <span className="faint" style={{ fontSize: 11, whiteSpace: "nowrap" }}>
            {matches.length === 0 ? T("No match") : F("{0} of {1}", view.searchIndex >= 0 ? view.searchIndex + 1 : "-", matches.length)}
          </span>
        )}
      </div>
      <div ref={listRef} className="tree" tabIndex={0} onKeyDown={keyDown} role="tree" aria-multiselectable>
        {visible.length === 0 && <div className="tree-empty">{running ? T("Reading…") : T("No tables yet. Run a check to read the sheets.")}</div>}
        {visible.map(({ node, depth }) => {
          const open = shownExpanded.has(node.key);
          const isSelected = view.selected.includes(node.key);
          const saved = node.googleTab?.localCopyUtc ? new Date(node.googleTab.localCopyUtc) : null;
          const localNote = saved ? F("Local copy · {0}", saved.toTimeString().slice(0, 5)) : "";
          return (
            <div
              key={node.key}
              data-key={node.key}
              className={`tree-row ${isSelected ? "selected" : ""}`}
              style={{ paddingLeft: 6 + depth * 12 }}
              title={saved ? node.tooltip + "\n" + F("Local copy · updated {0}", saved.toLocaleString()) : node.tooltip}
              role="treeitem"
              aria-selected={isSelected}
              aria-expanded={node.children.length > 0 ? open : undefined}
              onMouseDown={(event) => event.button === 0 && click(event, node)}
              onDoubleClick={() => {
                const source = sourceOf(node);
                if (source) void openSource(source.workbook, source.sheet);
              }}
              onContextMenu={(event) => nodeMenu(event, node)}
            >
              <span
                className={`twisty ${open ? "open" : ""}`}
                onMouseDown={(event) => {
                  if (node.children.length === 0) return;
                  event.stopPropagation();
                  const next = new Set(expanded);
                  if (open) next.delete(node.key);
                  else next.add(node.key);
                  view.setExpanded(next);
                }}
              >
                {node.children.length > 0 && <Chevron />}
              </span>
              <NodeIcon name={node.icon} />
              <span className="label">
                <Highlight text={node.label} query={query} />
              </span>
              {(localNote || node.note) && <span className={localNote ? "note local-copy" : "note"}>{localNote || node.note}</span>}
            </div>
          );
        })}
      </div>
      {project?.config && model.tables.length > 0 && (
        // What is selected (the toolbar's "Build selection" builds it), and how much there is in all.
        <div className="sidebar-foot">
          <span className="fill ellipsis" title={build.empty ? undefined : build.sheetsTooltip}>
            {build.empty ? "" : build.label}
          </span>
          <span className="faint">
            {Count(model.tables.length, "table")} · {Count(model.enums.length, "enum")}
          </span>
        </div>
      )}
    </div>
  );
}
