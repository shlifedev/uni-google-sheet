import { useEffect, useMemo } from "react";
import { useApp } from "../store";
import { useTablesView } from "../tablesView";
import { buildTree, flattenTree } from "../lib/tree";
import { Split } from "./Split";
import { TableTree } from "./TableTree";
import { PreviewPane } from "./Preview";
import { ProblemsBar, ProblemsList } from "./Problems";

/** The tree on the left; the data on the right with the problems panel under it, as in the Unity window. */
export function TablesPage() {
  const { model, problemsOpen } = useApp();
  const view = useTablesView();
  const tree = useMemo(() => buildTree(model), [model]);
  const focused = useMemo(() => flattenTree(tree).find((entry) => entry.node.key === view.focus)?.node ?? null, [tree, view.focus]);

  // ⌘F / Ctrl+F opens the find bar of the table on show; Esc in it closes it.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "f") {
        event.preventDefault();
        view.setFind(true);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [view]);

  return (
    <Split
      direction="horizontal"
      size={view.sidebarWidth}
      min={180}
      max={560}
      onSize={view.setSidebarWidth}
      first={<TableTree tree={tree} />}
      second={
        <Split
          direction="vertical"
          size={view.problemsHeight}
          min={90}
          max={Math.max(120, window.innerHeight - 220)}
          onSize={view.setProblemsHeight}
          collapsed={!problemsOpen}
          first={<PreviewPane node={focused} />}
          second={
            <div className="problems">
              <ProblemsBar />
              {problemsOpen && <ProblemsList />}
            </div>
          }
        />
      }
    />
  );
}
