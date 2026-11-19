// What the tree's selection means for a build: which sheets are read again, which tables come out of it, and the
// words for it. The toolbar's "Build selection" and the line under the tree both show this.
import { useMemo } from "react";
import { useApp } from "./store";
import { useTablesView } from "./tablesView";
import { F, T } from "./i18n";
import { buildTree, describeSelection, flattenTree, isSelectionEmpty, onlyArguments, selectionImpact, selectionOf, type TreeNode } from "./lib/tree";

export function useSelectionBuild() {
  const model = useApp((state) => state.model);
  const selected = useTablesView((state) => state.selected);

  return useMemo(() => {
    const byKey = new Map(flattenTree(buildTree(model)).map((entry) => [entry.node.key, entry.node]));
    const nodes = selected.map((key) => byKey.get(key)).filter((node): node is TreeNode => !!node);
    const selection = selectionOf(nodes);
    const impact = selectionImpact(model, selection);

    // "2", "enum 1", or "2 · enum 1": what is built again.
    const count =
      impact.enums.length === 0
        ? String(impact.tables.length)
        : (impact.tables.length > 0 ? impact.tables.length + " · " : "") + "enum " + impact.enums.length;
    const sheets = [
      ...[...selection.workbooks].sort().map((name) => name + T(" (all sheets)")),
      ...[...selection.sheets.values()].map((entry) => entry.workbook + " / " + entry.sheet),
    ];

    return {
      empty: isSelectionEmpty(selection),
      only: onlyArguments(selection),
      label: describeSelection(nodes, selection),
      count,
      /** The sheets read again, one per line. */
      sheetsTooltip: T("Reads these again; every other sheet comes from the last build on this machine.\n") + sheets.join("\n"),
      buildTooltip:
        T("Read only the selected sheets again and write the result.") +
        "\n" +
        F("Tables built again: {0}", impact.tables.length > 0 ? impact.tables.join(", ") : "-") +
        (impact.enums.length > 0 ? "\n" + F("Enums read again: {0}", impact.enums.join(", ")) : ""),
    };
  }, [model, selected]);
}
