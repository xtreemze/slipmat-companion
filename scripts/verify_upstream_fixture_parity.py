#!/usr/bin/env python3
"""Verify mirrored Slipmat fixtures against a reviewed upstream Git snapshot."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path

SHA1_RE = re.compile(r"^[0-9a-f]{40}$")
COMMIT_RE = re.compile(r"^[0-9a-f]{40}$")


def git_blob_sha1(payload: bytes) -> str:
    header = f"blob {len(payload)}\0".encode("ascii")
    return hashlib.sha1(header + payload).hexdigest()


def load_declared_paths(path: Path) -> list[str]:
    result: list[str] = []
    for raw in path.read_text(encoding="utf-8").splitlines():
        value = raw.strip()
        if not value or value.startswith("#"):
            continue
        parts = Path(value).parts
        if value.startswith("/") or ".." in parts:
            raise SystemExit(f"invalid parity path: {value}")
        result.append(value)
    if not result:
        raise SystemExit(f"no parity paths declared in {path}")
    if len(result) != len(set(result)):
        raise SystemExit(f"duplicate parity path in {path}")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Verify checked-in companion fixtures match the reviewed Slipmat "
            "Git blob identities recorded in the provenance snapshot."
        )
    )
    parser.add_argument("--root", type=Path, default=Path("."))
    parser.add_argument(
        "--paths",
        type=Path,
        default=Path("schema/upstream-parity-files.txt"),
    )
    parser.add_argument(
        "--provenance",
        type=Path,
        default=Path("schema/upstream-parity-provenance.json"),
    )
    args = parser.parse_args()

    root = args.root.resolve()
    paths_file = args.paths if args.paths.is_absolute() else root / args.paths
    provenance_file = (
        args.provenance
        if args.provenance.is_absolute()
        else root / args.provenance
    )

    data = json.loads(provenance_file.read_text(encoding="utf-8"))
    if data.get("upstreamRepository") != "xtreemze/slipmat":
        raise SystemExit("unexpected upstream repository in provenance snapshot")
    commit = data.get("upstreamCommit")
    if not isinstance(commit, str) or not COMMIT_RE.fullmatch(commit):
        raise SystemExit("invalid upstream commit in provenance snapshot")
    expected = data.get("files")
    if not isinstance(expected, dict):
        raise SystemExit("provenance files must be an object")

    declared = load_declared_paths(paths_file)
    if set(declared) != set(expected):
        missing = sorted(set(declared) - set(expected))
        extra = sorted(set(expected) - set(declared))
        raise SystemExit(
            f"parity manifest/provenance mismatch; missing={missing}, extra={extra}"
        )

    failures: list[str] = []
    for relative in declared:
        expected_sha = expected.get(relative)
        if not isinstance(expected_sha, str) or not SHA1_RE.fullmatch(expected_sha):
            failures.append(f"invalid expected Git blob SHA-1: {relative}")
            continue

        local_path = root / relative
        if not local_path.is_file():
            failures.append(f"missing mirrored fixture: {relative}")
            continue

        actual_sha = git_blob_sha1(local_path.read_bytes())
        if actual_sha != expected_sha:
            failures.append(
                f"fixture provenance mismatch: {relative} "
                f"(expected {expected_sha}, got {actual_sha})"
            )

    if failures:
        print(
            "Canonical fixture provenance verification failed. "
            f"Reviewed upstream snapshot: xtreemze/slipmat@{commit}"
        )
        for failure in failures:
            print(f"- {failure}")
        return 1

    print(
        "Canonical fixture provenance verified against "
        f"xtreemze/slipmat@{commit}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
