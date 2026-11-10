#!/usr/bin/env python3
"""Create a deterministic Linux archive for the offline maintenance runner."""
from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
import os
import pathlib
import re
import tarfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+\.\d+$")
HELPERS = ("custody.py", "host_guard.py")


def source_date_epoch() -> int:
    try:
        epoch = int(os.environ.get("SOURCE_DATE_EPOCH", "315532800"))
    except ValueError as error:
        raise ValueError("SOURCE_DATE_EPOCH must be an integer") from error
    epoch = max(epoch, 315532800)
    if epoch >= 1 << 33:
        raise ValueError("SOURCE_DATE_EPOCH is outside the supported tar timestamp range")
    return epoch


def regular_file(path: pathlib.Path) -> bytes:
    if path.is_symlink() or not path.is_file():
        raise ValueError(f"required regular file missing: {path}")
    return path.read_bytes()


def package(publish_dir: pathlib.Path, output: pathlib.Path, version: str, rid: str) -> None:
    if not VERSION_RE.fullmatch(version):
        raise ValueError("version must use Jellyfin four-part form, e.g. 0.1.0.0")
    if rid not in {"linux-x64", "linux-arm64"}:
        raise ValueError("RID must be linux-x64 or linux-arm64")
    if publish_dir.is_symlink() or not publish_dir.is_dir():
        raise ValueError("publish directory must be a real directory")

    apphost_name = "Slipmat.MaintenanceRunner"
    candidates = [apphost_name, f"{apphost_name}.dll", f"{apphost_name}.deps.json", f"{apphost_name}.runtimeconfig.json"]
    candidates.extend(path.name for path in publish_dir.iterdir() if path.suffix == ".dll")
    candidates.extend(HELPERS)

    files: dict[str, tuple[bytes, int]] = {}
    for name in sorted(set(candidates)):
        source = ROOT / "MaintenanceRunner" / name if name in HELPERS else publish_dir / name
        content = regular_file(source)
        executable = name == apphost_name or name in HELPERS
        if name == apphost_name and not source.stat().st_mode & 0o111:
            raise ValueError("published runner apphost is not executable")
        files[name] = (content, 0o755 if executable else 0o644)

    try:
        runtime_target = json.loads(files[f"{apphost_name}.deps.json"][0])["runtimeTarget"]["name"]
    except (KeyError, TypeError, json.JSONDecodeError) as error:
        raise ValueError("published deps file has no runtime target") from error
    if not runtime_target.endswith(f"/{rid}"):
        raise ValueError(f"published runtime target does not match requested RID: {runtime_target}")

    checksums = "".join(
        f"{hashlib.sha256(content).hexdigest()}  {name}\n"
        for name, (content, _) in sorted(files.items())
    ).encode("ascii")
    files["SHA256SUMS"] = (checksums, 0o644)

    epoch = source_date_epoch()
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("wb") as raw:
        with gzip.GzipFile(fileobj=raw, mode="wb", filename="", mtime=epoch, compresslevel=9) as compressed:
            with tarfile.open(fileobj=compressed, mode="w", format=tarfile.USTAR_FORMAT) as archive:
                for name, (content, mode) in sorted(files.items()):
                    entry = tarfile.TarInfo(f"slipmat-maintenance/{name}")
                    entry.size = len(content)
                    entry.mode = mode
                    entry.mtime = epoch
                    entry.uid = entry.gid = 0
                    entry.uname = entry.gname = ""
                    archive.addfile(entry, io.BytesIO(content))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish-dir", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--rid", choices=("linux-x64", "linux-arm64"), required=True)
    args = parser.parse_args()
    try:
        package(args.publish_dir, args.output, args.version, args.rid)
    except (OSError, ValueError) as error:
        parser.error(str(error))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
