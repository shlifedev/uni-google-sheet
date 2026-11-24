#!/usr/bin/env python3
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "src" / "Editor" / "HolySheetText.cs"
TARGET = ROOT / "desktop" / "src" / "i18n" / "ko.unity.json"

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
