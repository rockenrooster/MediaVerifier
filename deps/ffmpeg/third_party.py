#!/usr/bin/env python3
"""Writes THIRD-PARTY.txt for the FFmpeg bundle (subplan 17, task FFB-07).

Input: the "<package> <version>" lines that build-ffmpeg.sh records from pkg-config inside the build image, and
deps/ffmpeg/third-party.json, which says where each package's licence text lives. Every linked package must be in
the table, and every licence text must be fetched, or the script fails, so a bundle never ships with an incomplete
notice. --check validates the mapping without fetching anything.
"""
import argparse
import json
import sys
import urllib.error
import urllib.request


def resolve(table, name):
    entry = table["packages"].get(name)
    seen = set()
    while entry is not None and "same" in entry:
        if entry["same"] in seen:
            raise ValueError(f"alias loop at {name}")
        seen.add(entry["same"])
        name = entry["same"]
        entry = table["packages"].get(name)
    return name, entry


def plan(table, lines):
    """The licence entries to write, in a stable order: linked packages, bundled code, toolchain."""
    packages = {}
    for line in lines:
        parts = line.split()
        if parts:
            packages[parts[0]] = parts[1] if len(parts) > 1 else ""
    if not packages:
        raise ValueError("no linked packages were recorded")

    entries = {}
    unknown = []
    for name, version in sorted(packages.items()):
        key, entry = resolve(table, name)
        if entry is None:
            unknown.append(name)
            continue
        entries.setdefault(key, (entry, packages.get(key, version)))
    if unknown:
        raise ValueError("no licence source for: " + ", ".join(unknown) + " (add them to deps/ffmpeg/third-party.json)")

    if "libjxl_cms" in packages and "lcms2" not in packages:
        entries["skcms"] = (table["bundledWhenMissing"]["skcms"], "")
    for key, entry in table["toolchain"].items():
        if key != "comment":
            entries[key] = (entry, "")
    return [(key, entry, version) for key, (entry, version) in entries.items()]


def fetch(urls, version):
    errors = []
    for url in urls:
        url = url.replace("{version}", version) if version else url
        if "{version}" in url:
            continue
        try:
            with urllib.request.urlopen(url, timeout=30) as response:
                text = response.read().decode("utf-8", errors="replace").strip()
                if text:
                    return url, text
        except (urllib.error.URLError, TimeoutError) as error:
            errors.append(f"{url}: {error}")
    raise RuntimeError("could not fetch a licence text: " + "; ".join(errors))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("packages", help="file with '<package> <version>' lines from build-ffmpeg.sh")
    parser.add_argument("--table", default="deps/ffmpeg/third-party.json")
    parser.add_argument("--out", default="THIRD-PARTY.txt")
    parser.add_argument("--check", action="store_true", help="validate the mapping only; fetch nothing")
    args = parser.parse_args()

    table = json.load(open(args.table, encoding="utf-8"))
    lines = open(args.packages, encoding="utf-8").read().splitlines()
    try:
        entries = plan(table, lines)
    except ValueError as error:
        print(f"third_party.py: {error}", file=sys.stderr)
        return 1

    if args.check:
        for key, entry, version in entries:
            print(f"{entry['name']} {version}".strip() + f" ({entry['licence']})")
        return 0

    sections = [
        "Third-party software in the MediaVerifier FFmpeg bundle",
        "",
        "FFmpeg itself is licensed under the GNU Lesser General Public License version 2.1 or later (LICENSE.txt).",
        "The libraries below are linked into its DLLs. Versions are those of the build image; the complete",
        "corresponding source of FFmpeg and the build script are attached to the bundle release.",
    ]
    for key, entry, version in entries:
        source, text = fetch(entry["urls"], version)
        title = f"{entry['name']} {version}".strip()
        sections += ["", "=" * 78, f"{title} - {entry['licence']}", f"Licence text from {source}", "=" * 78, "", text]
    open(args.out, "w", encoding="utf-8", newline="\n").write("\n".join(sections) + "\n")
    print(f"third_party.py: wrote {args.out} with {len(entries)} entries")
    return 0


if __name__ == "__main__":
    sys.exit(main())
