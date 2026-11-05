#!/usr/bin/env python3
"""Checks the translations in i18n/ and builds the phone's values-xx/strings.xml from them.

Each i18n/xx.txt has one line per line of i18n/en.txt, in the same order. Run this after changing any of them.
"""
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parent.parent
I18N = ROOT / "i18n"
RES = ROOT / "AuraMouseAndroid" / "app" / "src" / "main" / "res"
# Android's folder name for each language, where it differs from ours.
FOLDER = {"id": "in"}
PLACEHOLDER = re.compile(r"\{\d\}")


def lines(code):
    return (I18N / f"{code}.txt").read_text(encoding="utf-8").rstrip("\n").split("\n")


def check():
    english = lines("en")
    problems = []
    if len(english) != len(set(english)):
        problems.append("en.txt has duplicate lines")
    for path in sorted(I18N.glob("*.txt")):
        code = path.stem
        if code == "en":
            continue
        translated = lines(code)
        if len(translated) != len(english):
            problems.append(f"{code}: {len(translated)} lines, expected {len(english)}")
            continue
        for n, (e, t) in enumerate(zip(english, translated), 1):
            if sorted(PLACEHOLDER.findall(e)) != sorted(PLACEHOLDER.findall(t)):
                problems.append(f"{code}:{n}: placeholders differ: {t}")
            if ("\\n" in e) != ("\\n" in t):
                problems.append(f"{code}:{n}: line breaks differ: {t}")
            if ("Aura Mouse" in e) != ("Aura Mouse" in t):
                problems.append(f"{code}:{n}: app name differs: {t}")
            if not t.strip():
                problems.append(f"{code}:{n}: empty")
    return problems


def to_plain(android):
    """Android's %1$s and \\' forms, as written in en.txt."""
    text = android.replace("\\'", "'").replace('\\"', '"')
    kinds = {}
    for match in re.finditer(r"%(\d)\$([sd])", text):
        kinds[int(match.group(1)) - 1] = match.group(2)
    return re.sub(r"%(\d)\$[sd]", lambda m: "{" + str(int(m.group(1)) - 1) + "}", text), kinds


def to_android(text, kinds):
    text = re.sub(r"\{(\d)\}", lambda m: f"%{int(m.group(1)) + 1}${kinds.get(int(m.group(1)), 's')}", text)
    text = text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    return text.replace("\\", "\\\\").replace("\\\\n", "\\n").replace("'", "\\'").replace('"', '\\"')


def generate():
    missing = []
    base = ET.parse(RES / "values" / "strings.xml").getroot()
    english = lines("en")
    for path in sorted(I18N.glob("*.txt")):
        code = path.stem
        if code == "en":
            continue
        table = dict(zip(english, lines(code)))
        out = ['<?xml version="1.0" encoding="utf-8"?>', "<!-- Made by tools/gen_android_strings.py from i18n/. Change it there. -->", "<resources>"]
        for item in base:
            if item.get("translatable") == "false":
                continue
            plain, kinds = to_plain(item.text)
            if plain not in table:
                missing.append(plain)
                continue
            out.append(f'    <string name="{item.get("name")}">{to_android(table[plain], kinds)}</string>')
        out.append("</resources>")
        folder = RES / f"values-{FOLDER.get(code, code)}"
        folder.mkdir(exist_ok=True)
        (folder / "strings.xml").write_text("\n".join(out) + "\n", encoding="utf-8")
    return sorted(set(missing))


if __name__ == "__main__":
    problems = check()
    missing = generate()
    for p in problems:
        print("PROBLEM:", p)
    for m in missing:
        print("NOT IN en.txt:", m)
    print(f"{len(list(I18N.glob('*.txt')))} languages, {len(lines('en'))} lines, {len(problems)} problems, {len(missing)} phone strings missing")
    sys.exit(1 if problems or missing else 0)
