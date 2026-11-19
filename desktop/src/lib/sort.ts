// Sorting for the data view, ported from the Unity window (UgsWindow.SortRows / CompareNatural) so both sort alike.
import type { SortKind } from "../types";

export interface SortSpec {
  /** A column index, or "row" for the order the rows came in (sheet order). */
  column: number | "row";
  descending: boolean;
}

interface SortKey {
  missing: boolean;
  number: number;
  text: string;
}

function isDigit(code: number): boolean {
  return code >= 48 && code <= 57;
}

/**
 * Text order for people: case is ignored and runs of digits compare as numbers, so "Item2" comes before "Item10".
 * Other characters compare by code unit, which puts Hangul in 가나다 order.
 */
export function compareNatural(a: string, b: string): number {
  let i = 0;
  let j = 0;
  while (i < a.length && j < b.length) {
    if (isDigit(a.charCodeAt(i)) && isDigit(b.charCodeAt(j))) {
      let endA = i;
      while (endA < a.length && isDigit(a.charCodeAt(endA))) endA++;
      let endB = j;
      while (endB < b.length && isDigit(b.charCodeAt(endB))) endB++;
      // Leading zeros do not change the value; "007" equals "7" here, and the final ordinal check splits them.
      let startA = i;
      while (startA < endA - 1 && a[startA] === "0") startA++;
      let startB = j;
      while (startB < endB - 1 && b[startB] === "0") startB++;
      const lengthA = endA - startA;
      const lengthB = endB - startB;
      if (lengthA !== lengthB) return lengthA < lengthB ? -1 : 1;
      for (let k = 0; k < lengthA; k++) {
        const x = a.charCodeAt(startA + k);
        const y = b.charCodeAt(startB + k);
        if (x !== y) return x < y ? -1 : 1;
      }
      i = endA;
      j = endB;
      continue;
    }

    const x = a[i].toUpperCase();
    const y = b[j].toUpperCase();
    if (x !== y) return x < y ? -1 : 1;
    i++;
    j++;
  }

  const rest = a.length - i - (b.length - j);
  if (rest !== 0) return rest < 0 ? -1 : 1;
  return a < b ? -1 : a > b ? 1 : 0;
}

// A cell's sort value, read once per sort. Missing: empty, or not a number in a number column.
function sortKey(raw: string, kind: SortKind): SortKey {
  const text = raw.trim();
  if (text.length === 0) return { missing: true, number: 0, text: "" };
  switch (kind) {
    case "number": {
      // Only the leading number counts, so an enum value shown as "8  (1 << 3)" sorts as 8.
      const first = text.split(/\s/, 1)[0];
      const number = Number(first);
      return first.length > 0 && Number.isFinite(number) ? { missing: false, number, text } : { missing: true, number: 0, text };
    }
    case "bool": {
      // As the build reads booleans: 1 / true and 0 / false.
      const lower = text.toLowerCase();
      if (text === "1" || lower === "true") return { missing: false, number: 1, text };
      if (text === "0" || lower === "false") return { missing: false, number: 0, text };
      return { missing: true, number: 0, text };
    }
    default:
      return { missing: false, number: 0, text };
  }
}

/**
 * Orders rows by the given columns, first column first. Empty cells go last in either direction, as in a spreadsheet,
 * and ties keep the order the rows came in.
 */
export function sortRows<T>(
  rows: T[],
  sorts: SortSpec[],
  columnFor: (column: number) => { kind: SortKind; text: (row: T) => string } | null,
): T[] {
  const keys: { values: SortKey[] | null; kind: SortKind; descending: boolean }[] = [];
  for (const sort of sorts) {
    if (sort.column === "row") {
      keys.push({ values: null, kind: "number", descending: sort.descending });
      continue;
    }
    const column = columnFor(sort.column);
    if (!column) continue;
    // Parse each cell once here, not on every comparison: a table can have a few hundred thousand rows.
    keys.push({ values: rows.map((row) => sortKey(column.text(row) ?? "", column.kind)), kind: column.kind, descending: sort.descending });
  }

  if (keys.length === 0) return rows;

  const order = rows.map((_, index) => index);
  order.sort((a, b) => {
    for (const { values, kind, descending } of keys) {
      let result: number;
      if (values === null) {
        result = a - b;
      } else {
        const x = values[a];
        const y = values[b];
        if (x.missing || y.missing) {
          // Not flipped by the direction: blanks stay at the bottom.
          result = x.missing === y.missing ? 0 : x.missing ? 1 : -1;
          if (result !== 0) return result;
          continue;
        }
        result = kind === "text" ? compareNatural(x.text, y.text) : x.number < y.number ? -1 : x.number > y.number ? 1 : 0;
      }
      if (result !== 0) return descending ? -result : result;
    }
    return a - b;
  });
  return order.map((index) => rows[index]);
}

/** Case-insensitive "contains", as the find bar and the tree search match. */
export function containsText(text: string, part: string): boolean {
  return text.toLowerCase().includes(part.toLowerCase());
}
