#!/usr/bin/env python3
"""Build VPM/UPM archives and the corresponding VPM listing manifest."""

import argparse
import gzip
import hashlib
import io
import json
from pathlib import Path
import re
import tarfile
import zipfile


REPO_ROOT = Path(__file__).resolve().parents[1]
PACKAGE_ID = "dev.honomin.prefab-variant-parent-replacer"
PACKAGE_ROOT = REPO_ROOT / "Packages" / PACKAGE_ID
REPOSITORY_URL = "https://github.com/honomin3/PrefabVariantParentReplacer"
TAG_PREFIX = "prefab-variant-parent-replacer-v"


def package_files():
    manifest = json.loads((PACKAGE_ROOT / "package.json").read_text(encoding="utf-8"))
    if manifest["name"] != PACKAGE_ID:
        raise ValueError("Package name does not match the package directory")
    if not re.fullmatch(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", manifest["version"]):
        raise ValueError("Expected a stable semantic version, such as 1.0.0")
    if "zipSHA256" in manifest:
        raise ValueError("zipSHA256 belongs in the VPM listing, not package.json")

    files = []
    guids = set()
    for path in sorted(PACKAGE_ROOT.rglob("*")):
        if path.is_symlink():
            raise ValueError(f"Package must not contain symlinks: {path}")
        if path.suffix == ".meta":
            asset = path.with_suffix("")
            if not asset.exists():
                raise ValueError(f"Orphan meta file: {path}")
            match = re.search(r"^guid: ([0-9a-f]{32})$", path.read_text(encoding="utf-8"), re.MULTILINE)
            if not match or match[1] in guids:
                raise ValueError(f"Missing or duplicate Unity GUID: {path}")
            guids.add(match[1])
        elif not path.with_name(path.name + ".meta").is_file():
            raise ValueError(f"Missing Unity meta file: {path}")
        if path.is_file():
            files.append(path)

    asmdef = json.loads((PACKAGE_ROOT / "Editor/Honomin.PrefabVariantParentReplacer.Editor.asmdef")
                        .read_text(encoding="utf-8"))
    if asmdef.get("includePlatforms") != ["Editor"]:
        raise ValueError("The tool assembly must be Editor-only")
    expected_readme = (REPO_ROOT / "README.md").read_text(encoding="utf-8").replace(
        f"](Packages/{PACKAGE_ID}/", "](")
    if (PACKAGE_ROOT / "README.md").read_text(encoding="utf-8") != expected_readme:
        raise ValueError("Root and package README contents are out of sync")
    if (PACKAGE_ROOT / "LICENSE").read_bytes() != (REPO_ROOT / "LICENSE").read_bytes():
        raise ValueError("Root and package licenses are out of sync")
    return manifest, files


def build(output, tag=None):
    manifest, files = package_files()
    expected_tag = TAG_PREFIX + manifest["version"]
    if tag is not None and tag != expected_tag:
        raise ValueError(f"Release tag must match package.json: {expected_tag}")

    output.mkdir(parents=True, exist_ok=True)
    basename = f"{PACKAGE_ID}-{manifest['version']}"
    zip_path = output / f"{basename}.zip"
    tgz_path = output / f"{basename}.tgz"

    # VPM requires package.json at the ZIP root. Fixed metadata makes builds reproducible.
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in files:
            entry = zipfile.ZipInfo(path.relative_to(PACKAGE_ROOT).as_posix(), (1980, 1, 1, 0, 0, 0))
            entry.create_system = 3
            entry.external_attr = 0o100644 << 16
            archive.writestr(entry, path.read_bytes(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)

    # UPM tarballs wrap the same files in a package/ directory.
    with tgz_path.open("wb") as raw:
        with gzip.GzipFile(fileobj=raw, mode="wb", filename="", mtime=0) as compressed:
            with tarfile.open(fileobj=compressed, mode="w", format=tarfile.USTAR_FORMAT) as archive:
                for path in files:
                    data = path.read_bytes()
                    entry = tarfile.TarInfo("package/" + path.relative_to(PACKAGE_ROOT).as_posix())
                    entry.size = len(data)
                    entry.mode = 0o644
                    archive.addfile(entry, io.BytesIO(data))

    checksums = {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in [zip_path, tgz_path]}
    (output / "SHA256SUMS").write_text(
        "".join(f"{checksum}  {name}\n" for name, checksum in checksums.items()), encoding="utf-8")
    listing = dict(manifest)
    listing["url"] = f"{REPOSITORY_URL}/releases/download/{expected_tag}/{zip_path.name}"
    listing["zipSHA256"] = checksums[zip_path.name]
    (output / "vpm-package.json").write_text(
        json.dumps(listing, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Built {basename}: {len(files)} files per archive")
    print(f"VPM ZIP SHA-256: {listing['zipSHA256']}")
    return listing


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=REPO_ROOT / "dist")
    parser.add_argument("--tag", help="Validate a release tag against package.json")
    args = parser.parse_args()
    try:
        build(args.output.resolve(), args.tag)
    except ValueError as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
