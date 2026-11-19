// What the Tables page shows: the tree's selection, open nodes and search, the panel sizes, the find bar.
// Kept apart from the app store so a run (which replaces the model) keeps it, and switching pages does too.
import { create } from "zustand";

interface TablesView {
  selected: string[];
  /** The node the preview shows: the one clicked last. */
  focus: string | null;
  /** The end of a Shift+click range. */
  anchor: string | null;
  expanded: Set<string> | null;
  search: string;
  searchIndex: number;
  sidebarWidth: number;
  problemsHeight: number;
  findOpen: boolean;
  findQuery: string;

  select: (keys: string[], focus: string | null, anchor?: string | null) => void;
  setExpanded: (expanded: Set<string>) => void;
  toggleExpanded: (key: string, open?: boolean) => void;
  setSearch: (search: string) => void;
  setSearchIndex: (index: number) => void;
  setSidebarWidth: (width: number) => void;
  setProblemsHeight: (height: number) => void;
  setFind: (open: boolean, query?: string) => void;
  reset: () => void;
}

export const useTablesView = create<TablesView>((set, get) => ({
  selected: [],
  focus: null,
  anchor: null,
  expanded: null,
  search: "",
  searchIndex: -1,
  sidebarWidth: 248,
  problemsHeight: 160,
  findOpen: false,
  findQuery: "",

  select: (selected, focus, anchor) => set({ selected, focus, anchor: anchor === undefined ? get().anchor : anchor }),
  setExpanded: (expanded) => set({ expanded }),
  toggleExpanded: (key, open) => {
    const expanded = new Set(get().expanded ?? []);
    const shouldOpen = open ?? !expanded.has(key);
    if (shouldOpen) expanded.add(key);
    else expanded.delete(key);
    set({ expanded });
  },
  setSearch: (search) => set({ search, searchIndex: -1 }),
  setSearchIndex: (searchIndex) => set({ searchIndex }),
  setSidebarWidth: (sidebarWidth) => set({ sidebarWidth }),
  setProblemsHeight: (problemsHeight) => set({ problemsHeight }),
  setFind: (findOpen, findQuery) => set({ findOpen, findQuery: findQuery ?? get().findQuery }),
  reset: () => set({ selected: [], focus: null, anchor: null, expanded: null, search: "", searchIndex: -1, findOpen: false, findQuery: "" }),
}));
