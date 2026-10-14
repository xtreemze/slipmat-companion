#!/usr/bin/env python3
"""Measure real reversible JPEG-to-JXL recompression without changing sources.

Requires libjxl 0.12+ cjxl and djxl. The strict djxl --reconstruct_jpeg
flag was introduced in 0.12. No alternate lossy JPEG decoder is allowed.
This is an operator-run corpus probe, not a Jellyfin request handler.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
from typing import Callable

MAX_INPUT_BYTES = 16 * 1024 * 1024
Runner = Callable[[list[str], int], bool]


def run_codec(arguments: list[str], seconds: int) -> bool:
    try:
        completed = subprocess.run(
            arguments,
            capture_output=True,
            check=False,
            timeout=seconds,
            shell=False,
        )
        return completed.returncode == 0
    except (OSError, subprocess.TimeoutExpired):
        return False


def jxl_signature(data: bytes) -> bool:
    return data.startswith(b"\xff\x0a") or data.startswith(
        bytes.fromhex("0000000c4a584c200d0a870a")
    )


def probe_jpeg(
    path: Path,
    *,
    encoder: str = "cjxl",
    decoder: str = "djxl",
    max_bytes: int = MAX_INPUT_BYTES,
    timeout: int = 30,
    runner: Runner = run_codec,
) -> dict[str, int | str]:
    """A result is 'verified' only after bit-identical JPEG reconstruction."""
    if path.is_symlink() or not path.is_file():
        return {"status": "invalid-source"}
    try:
        with path.open("rb") as source:
            jpeg = source.read(max_bytes + 1)
    except OSError:
        return {"status": "source-unreadable"}
    if len(jpeg) > max_bytes:
        return {"status": "source-too-large"}
    if len(jpeg) < 4 or not jpeg.startswith(b"\xff\xd8\xff"):
        return {"status": "not-jpeg"}

    with tempfile.TemporaryDirectory(prefix="slipmat-jxl-probe-") as root:
        directory = Path(root)
        source_path = directory / "source.jpeg"
        encoded_path = directory / "encoded.jxl"
        reconstructed_path = directory / "reconstructed.jpeg"
        source_path.write_bytes(jpeg)

        if not runner(
            [
                encoder, str(source_path), str(encoded_path),
                "--lossless_jpeg=1", "--allow_jpeg_reconstruction=1",
                "--num_threads=2",
            ],
            timeout,
        ):
            return {"status": "encode-failed", "jpegBytes": len(jpeg)}

        try:
            # Read one extra byte beyond source size; JXL larger than JPEG
            # is not eligible for the storage-saving representation.
            with encoded_path.open("rb") as handle:
                jxl = handle.read(len(jpeg) + 1)
        except OSError:
            return {"status": "missing-jxl", "jpegBytes": len(jpeg)}
        if not jxl_signature(jxl):
            return {"status": "invalid-jxl", "jpegBytes": len(jpeg)}
        if len(jxl) >= len(jpeg):
            return {"status": "no-size-benefit", "jpegBytes": len(jpeg)}

        if not runner(
            [
                decoder, str(encoded_path), str(reconstructed_path),
                "--reconstruct_jpeg", "--num_threads=2",
            ],
            timeout,
        ):
            return {"status": "reconstruction-failed", "jpegBytes": len(jpeg)}

        try:
            with reconstructed_path.open("rb") as handle:
                reconstructed = handle.read(len(jpeg) + 1)
        except OSError:
            return {"status": "missing-reconstruction", "jpegBytes": len(jpeg)}

        if len(reconstructed) != len(jpeg) or hashlib.sha256(
            reconstructed
        ).digest() != hashlib.sha256(jpeg).digest():
            return {"status": "mismatch", "jpegBytes": len(jpeg)}

        return {
            "status": "verified",
            "jpegBytes": len(jpeg),
            "jxlBytes": len(jxl),
            "savedBytes": len(jpeg) - len(jxl),
        }


def candidate_images(directory: Path, maximum: int):
    """Bound source reads; do not follow symlinks into other directories."""
    observed = 0
    for root, folders, files in os.walk(directory, followlinks=False):
        folders[:] = sorted(
            name for name in folders if not (Path(root) / name).is_symlink()
        )
        for name in sorted(files):
            path = Path(root) / name
            if path.suffix.lower() not in {".jpg", ".jpeg"} or path.is_symlink():
                continue
            yield path
            observed += 1
            if observed >= maximum:
                return


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path, help="Local, operator-selected JPEG sample folder")
    parser.add_argument("--cjxl", default="cjxl", help="Trusted libjxl 0.12+ encoder executable")
    parser.add_argument("--djxl", default="djxl", help="Trusted libjxl 0.12+ decoder executable")
    parser.add_argument("--max-files", type=int, default=20)
    parser.add_argument("--max-bytes", type=int, default=MAX_INPUT_BYTES)
    parser.add_argument("--timeout", type=int, default=30, help="Per-process deadline in seconds")
    args = parser.parse_args()

    if not args.directory.is_dir():
        parser.error("directory must exist")
    if not 1 <= args.max_files <= 1000:
        parser.error("--max-files must be within 1..1000")
    if not 1 <= args.max_bytes <= MAX_INPUT_BYTES:
        parser.error("--max-bytes must be within 1..16777216")
    if not 1 <= args.timeout <= 60:
        parser.error("--timeout must be within 1..60")

    results: list[dict[str, int | str]] = []
    for path in candidate_images(args.directory, args.max_files):
        result = probe_jpeg(
            path,
            encoder=args.cjxl,
            decoder=args.djxl,
            max_bytes=args.max_bytes,
            timeout=args.timeout,
        )
        results.append({"file": str(path.relative_to(args.directory)), **result})

    verified = [row for row in results if row["status"] == "verified"]
    print(json.dumps(
        {
            "tool": "jxl-reversible-corpus-v1",
            "inspected": len(results),
            "verified": len(verified),
            "jpegBytes": sum(int(row["jpegBytes"]) for row in verified),
            "jxlBytes": sum(int(row["jxlBytes"]) for row in verified),
            "results": results,
        },
        indent=2,
        sort_keys=True,
    ))
    # Other formats and incompressible JPEGs are expected data points.
    # A mismatch or zero verifiable conversions is a failed verification run.
    # Non-beneficial images remain explicit sample results, never a success claim.
    return 1 if not verified or any(row["status"] == "mismatch" for row in results) else 0


if __name__ == "__main__":
    raise SystemExit(main())
