"""Release gates shared by build and publish; standard library only."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile

PACKAGE_ID = "Db2i.AdoNet"
PROJECT = "src/Db2i/Db2i.csproj"
REPOSITORY = "https://github.com/bostick23/ibm.db2.dotnet"
VERSION = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def validate_version(tag, version, prerelease):
    match = VERSION.fullmatch(version)
    require(match is not None, "Package version must be SemVer without build metadata.")
    identifiers = (match.group(4) or "").split(".")
    require(all(not x.isdigit() or x == "0" or not x.startswith("0") for x in identifiers),
            "Numeric prerelease identifiers cannot have leading zeroes.")
    require(tag == f"v{version}", f"Release tag must be v{version}.")
    require(prerelease == bool(match.group(4)), "GitHub prerelease flag must match the package version.")


def validate_git(tag, main_ref="refs/remotes/origin/main", cwd=None):
    def git(*arguments):
        return subprocess.run(["git", *arguments], cwd=cwd, text=True,
                              capture_output=True, check=True).stdout.strip()

    commit = git("rev-parse", "HEAD")
    tagged = git("rev-parse", "--verify", f"refs/tags/{tag}^{{commit}}")
    require(tagged == commit, "The release tag no longer points to the checked-out commit.")
    git("rev-parse", "--verify", main_ref)
    result = subprocess.run(["git", "merge-base", "--is-ancestor", commit, main_ref], cwd=cwd)
    require(result.returncode == 0, "Release commit must belong to the history of main.")
    return commit


def validate_packages(directory, version, commit):
    require(VERSION.fullmatch(version) is not None, "Invalid package version.")
    require(re.fullmatch(r"[0-9a-f]{40}", commit) is not None, "Invalid release commit.")
    directory = Path(directory)
    expected = {f"{PACKAGE_ID}.{version}.{extension}" for extension in ("nupkg", "snupkg")}
    actual = {path.name for path in directory.iterdir() if path.suffix in (".nupkg", ".snupkg")}
    require(actual == expected, "Expected exactly one package and its matching symbol package.")
    checksums = {}
    for name in sorted(expected):
        path = directory / name
        with zipfile.ZipFile(path) as package:
            names = set(package.namelist())
            manifests = [name for name in names if name.endswith(".nuspec")]
            require(len(manifests) == 1, "Package must contain exactly one NuSpec.")
            metadata = ET.fromstring(package.read(manifests[0])).find("{*}metadata")
            require(metadata is not None, "Package metadata is missing.")
            require(metadata.findtext("{*}id") == PACKAGE_ID, "Unexpected package ID.")
            require(metadata.findtext("{*}version") == version, "Unexpected package version.")
            repository = metadata.find("{*}repository")
            require(repository is not None and repository.get("commit") == commit
                    and repository.get("url", "").removesuffix(".git") == REPOSITORY,
                    "Package repository metadata must identify the release commit.")
            if path.suffix == ".nupkg":
                for required in ("README.md", "LICENSE.md", "NOTICE.md",
                                 "lib/net8.0/Db2i.dll", "lib/net10.0/Db2i.dll",
                                 "lib/net8.0/Db2i.xml", "lib/net10.0/Db2i.xml"):
                    require(required in names, f"Package is missing {required}.")
                require(metadata.findtext("{*}readme") == "README.md", "Package README is not configured.")
                require(metadata.findtext("{*}license") == "IPL-1.0", "Unexpected package license.")
            else:
                source_url = f"https://raw.githubusercontent.com/bostick23/ibm.db2.dotnet/{commit}/".encode()
                for framework in ("net8.0", "net10.0"):
                    pdb = f"lib/{framework}/Db2i.pdb"
                    require(pdb in names, f"Symbols are missing {pdb}.")
                    require(source_url in package.read(pdb), f"Source Link in {pdb} must reference the release commit.")
        checksums[name] = hashlib.sha256(path.read_bytes()).hexdigest()
    return "".join(f"{digest}  {name}\n" for name, digest in sorted(checksums.items()))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    release = commands.add_parser("release")
    release.add_argument("--tag", required=True)
    release.add_argument("--prerelease", choices=("true", "false"), required=True)
    packages = commands.add_parser("packages")
    packages.add_argument("--directory", required=True)
    packages.add_argument("--version", required=True)
    packages.add_argument("--commit", required=True)
    checksum_mode = packages.add_mutually_exclusive_group(required=True)
    checksum_mode.add_argument("--write-checksums", action="store_true")
    checksum_mode.add_argument("--verify-checksums", action="store_true")
    args = parser.parse_args()
    if args.command == "release":
        result = subprocess.run(["dotnet", "msbuild", PROJECT, "-nologo",
                                 "-getProperty:PackageVersion", "-getProperty:PackageId"],
                                text=True, capture_output=True, check=True)
        properties = json.loads(result.stdout)["Properties"]
        require(properties["PackageId"] == PACKAGE_ID, "Unexpected project PackageId.")
        version = properties["PackageVersion"]
        validate_version(args.tag, version, args.prerelease == "true")
        commit = validate_git(args.tag)
        if os.environ.get("GITHUB_OUTPUT"):
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
                output.write(f"version={version}\n")
        print(f"Verified {PACKAGE_ID} {version} at {commit}.")
    else:
        checksums = validate_packages(args.directory, args.version, args.commit)
        path = Path(args.directory) / "SHA256SUMS"
        if args.write_checksums:
            path.write_text(checksums, encoding="utf-8")
        else:
            require(path.read_text(encoding="utf-8") == checksums, "Downloaded package checksums differ from the verified build.")
        print(f"Verified both {PACKAGE_ID} {args.version} packages and Source Link.")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError, ET.ParseError, zipfile.BadZipFile) as error:
        print(f"Release validation failed: {error}", file=sys.stderr)
        sys.exit(1)
