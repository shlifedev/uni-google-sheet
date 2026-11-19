import { describe, expect, it } from "vitest";
import { compareNatural, sortRows } from "../lib/sort";

describe("compareNatural", () => {
  it("compares digit runs as numbers and ignores case", () => {
    const names = ["Item10", "item2", "Item1", "가나", "Item02", "B", "a"];
    expect([...names].sort(compareNatural)).toEqual(["a", "B", "Item1", "Item02", "item2", "Item10", "가나"]);
  });

  it("splits equal values by their text, so the order is total", () => {
    expect(compareNatural("007", "7")).not.toBe(0);
    expect(compareNatural("abc", "abc")).toBe(0);
  });
});

describe("sortRows", () => {
  const rows = [["3"], [""], ["10"], ["x"], ["2"]];
  const column = (kind: "number" | "text") => () => ({ kind, text: (row: string[]) => row[0] });

  it("sorts numbers by value and keeps blanks and non-numbers last in both directions", () => {
    expect(sortRows(rows, [{ column: 0, descending: false }], column("number")).map((row) => row[0])).toEqual(["2", "3", "10", "", "x"]);
    expect(sortRows(rows, [{ column: 0, descending: true }], column("number")).map((row) => row[0])).toEqual(["10", "3", "2", "", "x"]);
  });

  it("is stable and puts sheet order back when sorted by row", () => {
    const tied = [["a", "1"], ["b", "1"], ["c", "0"]];
    const byValue = sortRows(tied, [{ column: 1, descending: false }], () => ({ kind: "number", text: (row) => row[1] }));
    expect(byValue.map((row) => row[0])).toEqual(["c", "a", "b"]);
    expect(sortRows(tied, [{ column: "row", descending: true }], () => null).map((row) => row[0])).toEqual(["c", "b", "a"]);
  });

  it("reads booleans as the build does", () => {
    const flags = [["true"], ["0"], ["1"], ["FALSE"], ["maybe"]];
    const sorted = sortRows(flags, [{ column: 0, descending: false }], () => ({ kind: "bool", text: (row) => row[0] }));
    expect(sorted.map((row) => row[0])).toEqual(["0", "FALSE", "true", "1", "maybe"]);
  });
});
