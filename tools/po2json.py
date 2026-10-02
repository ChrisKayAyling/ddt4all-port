#!/usr/bin/env python3
"""Convert the gettext .po catalogs of the Python ddt4all project into compact
flat JSON ({"msgid": "msgstr"}) files embedded into Ddt4All.App.

Usage: tools/po2json.py <ddt4all-python-checkout>/locales [out_dir]
Default out_dir: src/Ddt4All.App/Localization/Locales

Rules: untranslated / fuzzy entries and entries where msgstr == msgid are dropped
(runtime falls back to the English key). Locale dir names (fr, cs_CZ, uk_UA) become
file names with '_' -> '-' (fr.json, cs-CZ.json, uk-UA.json). The English catalog
is the key itself, so no en file is produced.
"""
import json, re, sys, pathlib

def unq(s):
    return json.loads(s) if s.startswith('"') else s

def parse(path):
    entries, cur, field, fuzzy = {}, {"msgid": None, "msgstr": None, "ctx": None}, None, False
    def flush():
        nonlocal cur, fuzzy
        if cur["msgid"] and cur["msgstr"] and not fuzzy and cur["msgstr"] != cur["msgid"]:
            key = cur["msgid"] if not cur["ctx"] else cur["ctx"] + "\x04" + cur["msgid"]
            entries[key] = cur["msgstr"]
        cur, fuzzy = {"msgid": None, "msgstr": None, "ctx": None}, False
    for raw in pathlib.Path(path).read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line:
            flush(); field = None; continue
        if line.startswith("#,") and "fuzzy" in line: fuzzy = True; continue
        if line.startswith("#"): continue
        m = re.match(r'(msgctxt|msgid|msgstr)\s+(".*")$', line)
        if m:
            field = {"msgctxt": "ctx"}.get(m.group(1), m.group(1))
            if field == "msgid" and cur["msgid"] is not None: flush()
            cur[field] = unq(m.group(2)); continue
        if line.startswith('"') and field:
            cur[field] = (cur[field] or "") + unq(line)
    flush()
    return entries

def main():
    src = pathlib.Path(sys.argv[1])
    out = pathlib.Path(sys.argv[2] if len(sys.argv) > 2 else "src/Ddt4All.App/Localization/Locales")
    out.mkdir(parents=True, exist_ok=True)
    for po in sorted(src.glob("*/LC_MESSAGES/*.po")):
        code = po.parent.parent.name.replace("_", "-")
        data = parse(po)
        data.pop("", None)  # header
        (out / f"{code}.json").write_text(
            json.dumps(data, ensure_ascii=False, separators=(",", ":"), sort_keys=True), encoding="utf-8")
        print(f"{code}: {len(data)} entries")

if __name__ == "__main__":
    main()
