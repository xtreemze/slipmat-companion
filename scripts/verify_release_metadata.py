#!/usr/bin/env python3
from __future__ import annotations

import argparse
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parent.parent
FIELD_RE = re.compile(r'^([A-Za-z][A-Za-z0-9]*):\s*"([^"]*)"\s*$')
GUID_RE = re.compile(r'Guid StaticId = new\("([^"]+)"\)')
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+\.\d+$")


def yaml_fields(path: pathlib.Path) -> dict[str, str]:
    values: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        match = FIELD_RE.match(line)
        if match:
            values[match.group(1)] = match.group(2)
    return values


def xml_value(path: pathlib.Path, tag: str) -> str:
    root = ET.parse(path).getroot()
    element = root.find(f".//{tag}")
    if element is None or not element.text:
        raise AssertionError(f"{path.name}: missing {tag}")
    return element.text.strip()


def package_versions(path: pathlib.Path) -> dict[str, str]:
    root = ET.parse(path).getroot()
    return {
        element.attrib["Include"]: element.attrib["Version"]
        for element in root.findall(".//PackageReference")
        if "Include" in element.attrib and "Version" in element.attrib
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify standalone Jellyfin release metadata.")
    parser.add_argument(
        "--expected-version",
        help="Require the requested release version to match reviewed source metadata.",
    )
    args = parser.parse_args()

    errors: list[str] = []
    build = yaml_fields(ROOT / "build.yaml")
    project = ROOT / "Jellyfin.Plugin.AudioGateway.csproj"

    for key in ("name", "guid", "version", "targetAbi", "framework", "owner", "changelog"):
        if not build.get(key):
            errors.append(f"build.yaml missing {key}")

    if build.get("version") and not VERSION_RE.fullmatch(build["version"]):
        errors.append("build.yaml version must be four-part")

    default_version = xml_value(ROOT / "Directory.Build.props", "Version")
    if build.get("version") != default_version:
        errors.append(
            f"build.yaml version {build.get('version')} != Directory.Build.props {default_version}"
        )

    if args.expected_version is not None:
        if not VERSION_RE.fullmatch(args.expected_version):
            errors.append("--expected-version must use four-part Jellyfin form")
        elif build.get("version") != args.expected_version:
            errors.append(
                f"requested release {args.expected_version} != reviewed build.yaml version "
                f"{build.get('version')}"
            )

    framework = xml_value(project, "TargetFramework")
    if build.get("framework") != framework:
        errors.append(f"build.yaml framework {build.get('framework')} != project {framework}")

    assembly = xml_value(project, "AssemblyName")
    artifact = f'- "{assembly}.dll"'
    if artifact not in (ROOT / "build.yaml").read_text(encoding="utf-8"):
        errors.append(f"build.yaml artifacts must contain {assembly}.dll")

    plugin_text = (ROOT / "Plugin.cs").read_text(encoding="utf-8")
    match = GUID_RE.search(plugin_text)
    if not match:
        errors.append("Plugin.cs stable GUID not found")
    elif build.get("guid", "").lower() != match.group(1).lower():
        errors.append(f"build.yaml GUID {build.get('guid')} != Plugin.cs {match.group(1)}")

    target_abi = build.get("targetAbi", "")
    package_target = ".".join(target_abi.split(".")[:3])
    packages = package_versions(project)
    for package in ("Jellyfin.Controller", "Jellyfin.Model"):
        if packages.get(package) != package_target:
            errors.append(
                f"{package} {packages.get(package)} != target ABI package line {package_target}"
            )

    if errors:
        print("release metadata verification failed:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1

    print(
        f"release metadata OK: {build['name']} {build['version']} "
        f"for Jellyfin ABI {build['targetAbi']} / {build['framework']}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
