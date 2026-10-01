#!/usr/bin/env python3
from __future__ import annotations

import argparse
import os
import pathlib
import re
import zipfile

VERSION_RE = re.compile(r"^\d+\.\d+\.\d+\.\d+$")


def main() -> int:
    parser = argparse.ArgumentParser(description="Create deterministic Audio Gateway plugin ZIP.")
    parser.add_argument("--input", required=True, help="Published Jellyfin.Plugin.AudioGateway.dll")
    parser.add_argument("--output", required=True, help="ZIP path to create")
    parser.add_argument("--version", required=True, help="Four-part plugin version")
    args = parser.parse_args()

    if not VERSION_RE.fullmatch(args.version):
        raise SystemExit("version must use Jellyfin four-part form, e.g. 0.1.0.0")

    source = pathlib.Path(args.input)
    if source.name != "Jellyfin.Plugin.AudioGateway.dll" or not source.is_file():
        raise SystemExit("input must be an existing Jellyfin.Plugin.AudioGateway.dll")

    output = pathlib.Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)

    epoch = int(os.environ.get("SOURCE_DATE_EPOCH", "315532800"))
    # ZIP cannot represent dates before 1980-01-01.
    if epoch < 315532800:
        epoch = 315532800
    import datetime
    dt = datetime.datetime.fromtimestamp(epoch, datetime.timezone.utc)
    zip_dt = (dt.year, dt.month, dt.day, dt.hour, dt.minute, dt.second - (dt.second % 2))

    info = zipfile.ZipInfo(source.name, date_time=zip_dt)
    info.compress_type = zipfile.ZIP_DEFLATED
    info.external_attr = 0o644 << 16
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        archive.writestr(info, source.read_bytes())

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
