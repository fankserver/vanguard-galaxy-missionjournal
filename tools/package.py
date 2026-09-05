#!/usr/bin/env python3
"""Owner-local packaging, or asset-free validation of a release zip."""
import argparse
from pathlib import Path
import stat
import zipfile

NAMES = {"VGMissionJournal.dll", "Newtonsoft.Json.dll", "README.md", "LICENSE", "THIRD_PARTY_NOTICES.md", "docs/api.md", "docs/lifecycle-migration.md"}


def validate(path):
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        expected = {"VGMissionJournal/" + name for name in NAMES}
        if len(entries) != len(expected) or {e.filename for e in entries} != expected:
            raise ValueError("Release file allowlist mismatch")
        if any(stat.S_ISLNK(e.external_attr >> 16) or e.file_size == 0 for e in entries):
            raise ValueError("Empty file or symbolic link in release")
        if archive.testzip() is not None:
            raise ValueError("Archive CRC failure")
    print("PASS: release zip file allowlist (not binary provenance)")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", type=Path)
    parser.add_argument("--configuration", default="Release")
    args = parser.parse_args()
    if args.check:
        validate(args.check)
    else:
        root = Path(__file__).resolve().parents[1]
        output = root / "VGMissionJournal/bin" / args.configuration / "netstandard2.1"
        if {p.name for p in output.glob("*.dll")} != {"VGMissionJournal.dll", "Newtonsoft.Json.dll"}:
            raise ValueError("Unexpected runtime DLL set; clean and rebuild")
        target = root / "dist/VGMissionJournal.zip"
        target.parent.mkdir(exist_ok=True)
        with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as archive:
            for name in sorted(NAMES):
                source = (output if name.endswith(".dll") else root) / name
                if source.is_symlink():
                    raise ValueError("Do not package linked inputs")
                archive.write(source, "VGMissionJournal/" + name)
        validate(target)
        print(target)
