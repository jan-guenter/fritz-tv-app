#!/usr/bin/env python3
"""Collect restored package versions, licence text and attribution for a release."""

from __future__ import annotations

import argparse
import json
import shutil
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path


def metadata(package: Path) -> tuple[ET.Element, Path]:
    manifest = next(package.glob("*.nuspec"))
    with manifest.open("rb") as stream:
        payload = stream.read(512 * 1024 + 1)
    if len(payload) > 512 * 1024 or b"<!DOCTYPE" in payload.upper():
        raise ValueError("NuGet metadata must be bounded and must not contain a DTD")
    # ElementTree never resolves external entities; rejecting DTDs also prevents internal entity expansion.
    document = ET.fromstring(payload)  # noqa: S314 - bounded, DTD-free package metadata only.
    item = document.find("{*}metadata")
    if item is None:
        raise ValueError(f"Missing NuGet metadata in {manifest.name}")
    return item, manifest


def collect(assets: Path, destination: Path, runtime: str) -> None:
    project = json.loads(assets.read_text(encoding="utf-8-sig"))
    folders = [Path(folder) for folder in project["packageFolders"]]
    packages = {key: entry["path"] for key, entry in project["libraries"].items() if entry["type"] == "package"}
    for framework in project["project"]["frameworks"].values():
        for item in framework.get("downloadDependencies", []):
            if item["name"].lower().endswith(".runtime." + runtime):
                version = item["version"].strip("[]").split(",")[0].strip()
                packages[item["name"] + "/" + version] = item["name"].lower() + "/" + version
    destination.mkdir(parents=True, exist_ok=True)
    inventory = []
    references: dict[str, bytes] = {}
    for identity, relative in sorted(packages.items(), key=lambda item: item[0].lower()):
        package = next((folder / relative for folder in folders if (folder / relative).is_dir()), None)
        if package is None:
            raise FileNotFoundError(f"Restored package missing: {identity}")
        item, manifest = metadata(package)
        name, version = identity.rsplit("/", 1)
        output = destination / name / version
        output.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(manifest, output / manifest.name)
        licence = item.find("{*}license")
        expression = ""
        if licence is not None and licence.text:
            if licence.get("type") == "file":
                source = (package / licence.text.replace("\\", "/")).resolve()
                if not source.is_relative_to(package.resolve()):
                    raise ValueError(f"Licence file escapes package: {identity}")
                shutil.copyfile(source, output / "LICENSE.txt")
            elif licence.get("type") == "expression":
                expression = licence.text
                url = "https://licenses.nuget.org/" + urllib.parse.quote(expression, safe="")
                if expression not in references:
                    with urllib.request.urlopen(url, timeout=30) as response:
                        content = response.read(2 * 1024 * 1024 + 1)
                    if len(content) > 2 * 1024 * 1024:
                        raise ValueError(f"Licence response too large: {identity}")
                    references[expression] = content
                (output / "LICENSE.reference.html").write_bytes(references[expression])
        for source in package.iterdir():
            if source.is_file() and source.name.upper().startswith(
                ("LICENSE", "LICENCE", "COPYING", "COPYRIGHT", "NOTICE", "THIRD-PARTY")
            ):
                shutil.copyfile(source, output / source.name)
        inventory.append(
            {
                "package": name,
                "version": version,
                "licence": expression or (licence.text if licence is not None else None),
                "authors": item.findtext("{*}authors"),
                "copyright": item.findtext("{*}copyright"),
                "project": item.findtext("{*}projectUrl"),
                "licenseUrl": item.findtext("{*}licenseUrl"),
            }
        )
    (destination / "packages.json").write_text(
        json.dumps(inventory, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("assets", type=Path)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--runtime", choices=("win-x64", "linux-x64"), required=True)
    args = parser.parse_args()
    collect(args.assets, args.destination, args.runtime)
