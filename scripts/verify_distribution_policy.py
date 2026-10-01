#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify the explicit public-distribution gate.")
    parser.add_argument(
        "--require-approved",
        action="store_true",
        help="Fail unless public distribution has been explicitly approved.",
    )
    args = parser.parse_args()

    policy_path = ROOT / "distribution-policy.json"
    policy = json.loads(policy_path.read_text(encoding="utf-8"))
    approved = policy.get("publicDistributionApproved")
    license_spdx = policy.get("licenseSpdx")

    if not isinstance(approved, bool):
        print("distribution-policy.json: publicDistributionApproved must be boolean", file=sys.stderr)
        return 1
    if license_spdx is not None and (not isinstance(license_spdx, str) or not license_spdx.strip()):
        print("distribution-policy.json: licenseSpdx must be null or a non-empty SPDX identifier", file=sys.stderr)
        return 1

    if approved and not license_spdx:
        print("approved public distribution requires an explicit licenseSpdx", file=sys.stderr)
        return 1

    if args.require_approved and not approved:
        print(
            "public distribution is not approved; choose and review a Jellyfin-compatible "
            "open-source license, update LICENSE and distribution-policy.json, then retry",
            file=sys.stderr,
        )
        return 1

    state = "approved" if approved else "not approved"
    print(f"public distribution policy: {state}; licenseSpdx={license_spdx!r}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
