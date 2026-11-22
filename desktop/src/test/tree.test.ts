import { beforeAll, describe, expect, it } from "vitest";
import { setLanguage } from "../i18n";
import { buildTree, describeSelection, flattenTree, onlyArguments, selectionImpact, selectionOf } from "../lib/tree";
import type { TableModel } from "../types";

const model: TableModel = {
  workbooks: [
    { name: "Game.xlsx", type: "xlsx", source: "Sheets", link: null },
    { name: "Csv", type: "csv", source: "Sheets/Csv", link: null },
  ],
  tables: [
    {
      name: "Item",
      kind: "table",
      location: "Game.xlsx / Item",
      workbook: "Game.xlsx",
      rows: 6,
      columns: 20,
      sheets: [
        { workbook: "Game.xlsx", sheet: "Item", role: "defines" },
        { workbook: "Game.xlsx", sheet: "Item.Rare", role: "rows" },
      ],
    },
    { name: "Monster", kind: "table", location: "Game.xlsx / Monster", workbook: "Game.xlsx", rows: 3, columns: 5, sheets: [{ workbook: "Game.xlsx", sheet: "Monster", role: "defines" }] },
    { name: "Limits", kind: "table", location: "Csv / Limits", workbook: "Csv", rows: 3, columns: 4, sheets: [{ workbook: "Csv", sheet: "Limits", role: "defines" }] },
  ],
  enums: [
    { name: "EItemKind", underlying: "byte", isFlags: false, comment: "", workbook: "Game.xlsx", sheet: "Enums", mapping: false, members: [] },
    { name: "EMonsterId", underlying: "int", isFlags: false, comment: "", workbook: "Game.xlsx", sheet: "Monster", mapping: true, members: [] },
  ],
};

beforeAll(() => setLanguage("en"));

describe("buildTree", () => {
  it("attaches copy controls to individual Google tabs, including tabs with no table", () => {
    const tab = (sheet: string) => ({ workbook: "Remote", sheet, localCopyUtc: null, localPath: null });
    const one = { ...model.tables[2], workbook: "Remote", sheets: [{ ...model.tables[2].sheets[0], workbook: "Remote" }] };
    const many = { ...model.tables[0], workbook: "Remote", sheets: model.tables[0].sheets.map((entry) => ({ ...entry, workbook: "Remote" })) };
    const tree = buildTree({ ...model, googleTabs: ["Item", "Item.Rare", "Limits", "Empty"].map(tab),
      workbooks: [...model.workbooks, { name: "Remote", type: "google", source: "test", link: null }], tables: [one, many, model.tables[1]] });
    const nodes = new Map(flattenTree(tree).map(({ node }) => [node.key, node.googleTab?.sheet]));
    expect(nodes.get("table:Limits")).toBe("Limits");
    expect(nodes.get("sheet:Item/Item.Rare")).toBe("Item.Rare");
    expect(nodes.get("tab:Remote/Empty")).toBe("Empty");
    expect(nodes.get("table:Item")).toBeUndefined();
    expect(nodes.get("table:Monster")).toBeUndefined();
    expect(nodes.get("workbook:Remote")).toBeUndefined();
  });

  it("groups by connection when there is more than one, with sheets and enums where they belong", () => {
    const tree = buildTree(model);
    expect(tree.map((node) => node.label)).toEqual(["Excel", "CSV"]);
    const keys = flattenTree(tree).map((entry) => entry.node.key);
    expect(keys).toEqual([
      "group:excel:Sheets",
      "workbook:Game.xlsx",
      "table:Item",
      "sheet:Item/Item",
      "sheet:Item/Item.Rare",
      "table:Monster",
      "enum:EMonsterId",
      "enumsheet:Game.xlsx/Enums",
      "enum:EItemKind",
      "group:csv:Sheets/Csv",
      "workbook:Csv",
      "table:Limits",
    ]);
  });

  it("leaves out the group level with a single connection", () => {
    const single = { ...model, workbooks: [model.workbooks[0]], tables: model.tables.slice(0, 2) };
    expect(buildTree(single)[0].key).toBe("workbook:Game.xlsx");
  });
});

describe("selection", () => {
  const nodes = new Map(flattenTree(buildTree(model)).map((entry) => [entry.node.key, entry.node]));

  it("turns a table into every sheet it was built from, and counts what is built again", () => {
    const selected = [nodes.get("table:Item")!];
    const selection = selectionOf(selected);
    expect(onlyArguments(selection)).toEqual(["Game.xlsx/Item", "Game.xlsx/Item.Rare"]);
    expect(selectionImpact(model, selection)).toEqual({ tables: ["Item"], enums: [] });
    expect(describeSelection(selected, selection)).toBe("Item · 2 sheets");
  });

  it("reads a whole workbook for a workbook node, enums sheets included", () => {
    const selected = [nodes.get("workbook:Game.xlsx")!, nodes.get("table:Limits")!];
    const selection = selectionOf(selected);
    expect(onlyArguments(selection)).toEqual(["Game.xlsx", "Csv/Limits"]);
    expect(selectionImpact(model, selection)).toEqual({ tables: ["Item", "Limits", "Monster"], enums: ["EItemKind"] });
    expect(describeSelection(selected, selection)).toBe("Game.xlsx + Limits");
  });
});
