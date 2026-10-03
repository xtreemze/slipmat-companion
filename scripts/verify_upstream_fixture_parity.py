#!/usr/bin/env python3
"""Compare the companion's mirrored canonical fixtures with Slipmat upstream."""

from __future__ import annotations

import argparse
import difflib
from pathlib import Path


def load_paths(manifest: Path) -> list[str]:
    paths: list[str] = []
    for raw in manifest.read_text(encoding="utf-8").splitlines():
        value = raw.strip()
        if not value or value.startswith("#"):
            continue
        if value.startswith("/") or ".." in Path(value).parts:
            raise SystemExit(f"invalid parity path: {value}")
        paths.append(value)
    if not paths:
        raise SystemExit(f"no parity paths declared in {manifest}")
    if len(paths) != len(set(paths)):
        raise SystemExit(f"duplicate parity path in {manifest}")
    return paths


def text_diff(left: bytes, right: bytes, path: str) -> str:
    try:
        left_text = left.decode("utf-8").splitlines(keepends=True)
        right_text = right.decode("utf-8").splitlines(keepends=True)
    except UnicodeDecodeError:
        return ""
    return "".join(
        difflib.unified_diff(
            left_text,
            right_text,
            fromfile=f"companion/{path}",
            tofile=f"slipmat/{path}",
        )
    )


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Verify checked-in companion fixtures match canonical Slipmat fixtures."
    )
    parser.add_argument("--companion-root", type=Path, default=Path("."))
    parser.add_argument("--upstream-root", type=Path, required=True)
    parser.add_argument(
        "--manifest",
        type=Path,
        default=Path("schema/upstream-parity-files.txt"),
    )
    args = parser.parse_args()

    companion_root = args.companion_root.resolve()
    upstream_root = args.upstream_root.resolve()
    manifest = args.manifest
    if not manifest.is_absolute():
        manifest = companion_root / manifest

    failures: list[str] = []
    for relative in load_paths(manifest):
        companion_path = companion_root / relative
        upstream_path = upstream_root / relative

        if not companion_path.is_file():
            failures.append(f"missing companion fixture: {relative}")
            continue
        if not upstream_path.is_file():
            failures.append(f"missing upstream fixture: {relative}")
            continue

        companion_bytes = companion_path.read_bytes()
        upstream_bytes = upstream_path.read_bytes()
        if companion_bytes == upstream_bytes:
            continue

        detail = text_diff(companion_bytes, upstream_bytes, relative)
        failures.append(
            f"fixture drift: {relative}" + (f"\n{detail}" if detail else "")
        )

    if failures:
        print(
            "Slipmat companion fixture parity failed. "
            "Update the mirrored fixture intentionally after reviewing the upstream contract."
        )
        for failure in failures:
            print(f"\n{failure}")
        return 1

    print("Slipmat companion fixture parity passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
