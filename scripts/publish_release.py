#!/usr/bin/env python3
"""Publish validated package archives from a release tag in GitHub Actions."""

import argparse
import hashlib
import json
from pathlib import Path
import subprocess

from build_package import PACKAGE_ID, PACKAGE_ROOT, REPO_ROOT, TAG_PREFIX


def gh(*args):
    result = subprocess.run(["gh", *args], capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(result.stderr.strip() or result.stdout.strip())
    return result.stdout.strip()


def publish(repository, tag, target):
    manifest = json.loads((PACKAGE_ROOT / "package.json").read_text(encoding="utf-8"))
    if tag != TAG_PREFIX + manifest["version"]:
        raise ValueError("Tag does not match the package version")
    dist = REPO_ROOT / "dist"
    basename = f"{PACKAGE_ID}-{manifest['version']}"
    archives = [dist / f"{basename}.zip", dist / f"{basename}.tgz"]
    checksum_file = dist / "SHA256SUMS"
    expected = dict(line.split("  ", 1)[::-1] for line in checksum_file.read_text().splitlines())
    for archive in archives:
        if hashlib.sha256(archive.read_bytes()).hexdigest() != expected[archive.name]:
            raise ValueError(f"Archive checksum mismatch: {archive.name}")
    notes = REPO_ROOT / "docs" / "RELEASE_NOTES_JA.md"
    if not notes.is_file():
        raise ValueError("Release notes are missing")

    existing = subprocess.run(
        ["gh", "release", "view", tag, "--repo", repository, "--json", "isDraft"],
        capture_output=True, text=True)
    if existing.returncode == 0:
        if not json.loads(existing.stdout)["isDraft"]:
            raise ValueError("This version is already published; its files will not be replaced")
        gh("release", "edit", tag, "--repo", repository, "--target", target,
           "--notes-file", str(notes))
    elif "release not found" in existing.stderr.lower():
        gh("release", "create", tag, "--repo", repository, "--target", target,
           "--verify-tag", "--draft", "--title", f"Prefab Variant Parent Replacer {manifest['version']}",
           "--notes-file", str(notes))
    else:
        raise RuntimeError(existing.stderr.strip())

    gh("release", "upload", tag, "--repo", repository, "--clobber",
       *(str(path) for path in [*archives, checksum_file, notes]))
    gh("release", "edit", tag, "--repo", repository, "--draft=false", "--latest")
    print(gh("release", "view", tag, "--repo", repository, "--json", "url", "--jq", ".url"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--target", required=True)
    parser.add_argument("--repository", required=True)
    args = parser.parse_args()
    publish(args.repository, args.tag, args.target)


if __name__ == "__main__":
    main()
