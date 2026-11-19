#!/usr/bin/env python3
"""Exports the Unity window's Korean texts (src/Editor/UgsText.cs) for the desktop app.

The window keys its translations by the English text, and the desktop app does the same, so both tools use one
set of translations. Writes desktop/src/i18n/ko.unity.json:
  {"counts": {"table": "테이블 {0}개", ...}, "texts": {"Tables": "테이블", ...}}
Texts only the desktop app has live in desktop/src/i18n/ko.app.json and win over these.

Run from anywhere: python3 tools/export-ugs-text.py  (or: npm run i18n, in desktop/)
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "src" / "Editor" / "UgsText.cs"
TARGET = ROOT / "desktop" / "src" / "i18n" / "ko.unity.json"

# ["key"] = "value", with C# escapes in either string.
ENTRY = re.compile(r'\["((?:[^"\\]|\\.)*)"\]\s*=\s*"((?:[^"\\]|\\.)*)"')


def unescape(text: str) -> str:
    return json.loads('"' + text + '"')


def dictionary(source: str, name: str) -> dict:
    start = source.find(f"Dictionary<string, string> {name} =")
    if start < 0:
        sys.exit(f"{SOURCE}: the dictionary '{name}' was not found.")
    body_start = source.index("{", source.index("new Dictionary", start))
    depth = 0
    for index in range(body_start, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                body = source[body_start:index]
                break
    return {unescape(key): unescape(value) for key, value in ENTRY.findall(body)}


def main() -> None:
    source = SOURCE.read_text(encoding="utf-8")
    exported = {"counts": dictionary(source, "KoreanCounts"), "texts": dictionary(source, "Korean")}
    TARGET.parent.mkdir(parents=True, exist_ok=True)
    TARGET.write_text(json.dumps(exported, ensure_ascii=False, indent=1, sort_keys=True) + "\n", encoding="utf-8")
    print(f"{TARGET.relative_to(ROOT)}: {len(exported['texts'])} texts, {len(exported['counts'])} counts")


if __name__ == "__main__":
    main()
