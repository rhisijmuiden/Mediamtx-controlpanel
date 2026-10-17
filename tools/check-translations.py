#!/usr/bin/env python3
"""Checks that every text wrapped in L["..."] has a translation for every language.

Usage: python3 tools/check-translations.py        (exit code 1 when translations are missing)
Translations live in src/MediaMtxPanel/Localization/*.json:
    { "English text": { "nl": "...", "de": "...", "fr": "..." } }
Texts passed to L[...] through a variable are not found by this check; keep those in the JSON files by hand.
"""
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent / "src" / "MediaMtxPanel"
LANGUAGES = ["nl", "de", "fr"]
KEY = re.compile(r'\bL\[\s*"((?:[^"\\]|\\.)*)"')

used = {}
for path in sorted(ROOT.rglob("*")):
    if path.suffix not in (".razor", ".cs") or "bin" in path.parts or "obj" in path.parts:
        continue
    for match in KEY.finditer(path.read_text(encoding="utf-8")):
        key = json.loads('"' + match.group(1) + '"')
        used.setdefault(key, path.relative_to(ROOT))

translations = {}
for file in sorted((ROOT / "Localization").glob("*.json")):
    for key, values in json.loads(file.read_text(encoding="utf-8")).items():
        translations.setdefault(key, {}).update({k: v for k, v in values.items() if v})

missing = [(key, where, [l for l in LANGUAGES if l not in translations.get(key, {})])
           for key, where in sorted(used.items(), key=lambda kv: str(kv[1]))]
missing = [m for m in missing if m[2]]
for key, where, langs in missing:
    print(f"{where}: missing {','.join(langs)}: {key}")

placeholders = re.compile(r"\{\d+\}")
for key, values in translations.items():
    for lang, text in values.items():
        if sorted(placeholders.findall(key)) != sorted(placeholders.findall(text)):
            print(f"placeholder mismatch ({lang}): {key!r} -> {text!r}")
            missing.append((key, "", [lang]))

print(f"{len(used)} texts in use, {len(translations)} translated, {len(missing)} problem(s).")
sys.exit(1 if missing else 0)
