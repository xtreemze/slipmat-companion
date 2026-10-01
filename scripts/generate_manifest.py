#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import re

FIELD_RE = re.compile(r'^([A-Za-z][A-Za-z0-9]*):\s*"([^"]*)"\s*$')
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+\.\d+$")
CHECKSUM_RE = re.compile(r"^[0-9a-f]{32}$")


def read_build_metadata(path: pathlib.Path) -> dict[str, str]:
    values: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        match = FIELD_RE.match(line)
        if match:
            values[match.group(1)] = match.group(2)
    required = {"name", "guid", "version", "targetAbi", "overview", "category", "owner"}
    missing = sorted(required - values.keys())
    if missing:
        raise SystemExit(f"build metadata is missing fields: {', '.join(missing)}")
    return values


def md5(path: pathlib.Path) -> str:
    digest = hashlib.md5(usedforsecurity=False)
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def read_previous_versions(
    path: pathlib.Path,
    metadata: dict[str, str],
    current_version: str,
) -> list[dict[str, str]]:
    previous = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(previous, list) or len(previous) != 1 or not isinstance(previous[0], dict):
        raise SystemExit("previous manifest must contain exactly one plugin entry")

    plugin = previous[0]
    if plugin.get("guid") != metadata["guid"] or plugin.get("name") != metadata["name"]:
        raise SystemExit("previous manifest plugin identity does not match build metadata")

    versions = plugin.get("versions")
    if not isinstance(versions, list):
        raise SystemExit("previous manifest versions must be a list")

    required_fields = {
        "version",
        "changelog",
        "targetAbi",
        "sourceUrl",
        "checksum",
        "timestamp",
    }
    retained: list[dict[str, str]] = []
    for item in versions:
        if not isinstance(item, dict) or set(item.keys()) != required_fields:
            raise SystemExit("previous manifest contains an invalid version entry")
        if any(not isinstance(item[field], str) for field in required_fields):
            raise SystemExit("previous manifest version fields must be strings")
        if not VERSION_RE.fullmatch(item["version"]):
            raise SystemExit("previous manifest contains an invalid version")
        if not CHECKSUM_RE.fullmatch(item["checksum"]):
            raise SystemExit("previous manifest contains an invalid checksum")
        if item["version"] != current_version:
            retained.append(item)

    return retained


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate Jellyfin repository manifest.json.")
    parser.add_argument("--build-file", default="build.yaml")
    parser.add_argument("--zip", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--source-url", required=True)
    parser.add_argument("--timestamp", required=True)
    parser.add_argument("--changelog-file", required=True)
    parser.add_argument("--previous-manifest")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    if not VERSION_RE.fullmatch(args.version):
        raise SystemExit("version must use Jellyfin four-part form")

    metadata = read_build_metadata(pathlib.Path(args.build_file))
    archive = pathlib.Path(args.zip)
    if not archive.is_file():
        raise SystemExit(f"missing release archive: {archive}")

    changelog = pathlib.Path(args.changelog_file).read_text(encoding="utf-8").strip()
    if not changelog:
        raise SystemExit("release changelog must not be empty")

    current_version = {
        "version": args.version,
        "changelog": changelog,
        "targetAbi": metadata["targetAbi"],
        "sourceUrl": args.source_url,
        "checksum": md5(archive),
        "timestamp": args.timestamp,
    }

    previous_versions: list[dict[str, str]] = []
    if args.previous_manifest:
        previous_versions = read_previous_versions(
            pathlib.Path(args.previous_manifest),
            metadata,
            args.version,
        )

    manifest = [
        {
            "guid": metadata["guid"],
            "name": metadata["name"],
            "description": (
                "Optional Slipmat Jellyfin companion for batching, precomputed analysis "
                "artifacts, bounded podcast acquisition, and synchronization. "
                "Slipmat remains authoritative and works without the companion."
            ),
            "overview": metadata["overview"],
            "owner": metadata["owner"],
            "category": metadata["category"],
            "versions": [current_version, *previous_versions],
        }
    ]

    output = pathlib.Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
