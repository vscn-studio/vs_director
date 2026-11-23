"""Check the install layout, binary architecture and source material in a CI package."""

import argparse
import hashlib
import io
import json
from pathlib import Path
import plistlib
import struct
import tarfile
import zipfile


def check_package(path, platform):
    if zipfile.is_zipfile(path):
        archive = zipfile.ZipFile(path)
        names = set(archive.namelist())
        read = archive.read
    else:
        archive = tarfile.open(path, "r:gz")
        names = set(archive.getnames())

        def read(name):
            stream = archive.extractfile(name)
            if stream is None:
                raise ValueError(f"Expected a regular file: {name}")
            return stream.read()

    with archive:
        binary, legal, targets = {
            "windows": ("obs-plugins/64bit/vsdirector-obs.dll",
                        "data/obs-plugins/vsdirector-obs", ["windows-x64"]),
            "linux": ("vsdirector-obs/bin/64bit/vsdirector-obs.so",
                      "vsdirector-obs/data", ["linux-x86_64"]),
            "macos": ("vsdirector-obs.plugin/Contents/MacOS/vsdirector-obs",
                      "vsdirector-obs.plugin/Contents/Resources", ["macos-arm64", "macos-x86_64"]),
        }[platform]

        def require(name):
            if name not in names:
                raise ValueError(f"Missing package file: {name}")
            data = read(name)
            if not data:
                raise ValueError(f"Empty package file: {name}")
            return data

        data = require(binary)
        if platform == "windows":
            pe = struct.unpack_from("<I", data, 0x3C)[0]
            if data[:2] != b"MZ" or data[pe:pe + 6] != b"PE\0\0\x64\x86":
                raise ValueError("Expected a Windows x64 PE binary")
        elif platform == "linux":
            if data[:6] != b"\x7fELF\x02\x01" or struct.unpack_from("<H", data, 18)[0] != 62:
                raise ValueError("Expected a Linux x86_64 ELF binary")
        else:
            magic, count = struct.unpack_from(">II", data)
            if magic not in (0xCAFEBABE, 0xCAFEBABF) or count != 2:
                raise ValueError("Expected a universal Mach-O binary with two architectures")
            stride = 20 if magic == 0xCAFEBABE else 32
            archs = {struct.unpack_from(">I", data, 8 + i * stride)[0] for i in range(count)}
            if archs != {0x01000007, 0x0100000C}:
                raise ValueError("Universal plugin must contain x86_64 and arm64")
            info = plistlib.loads(require("vsdirector-obs.plugin/Contents/Info.plist"))
            if info.get("CFBundleIdentifier") != "studio.vscn.vsdirector-obs":
                raise ValueError("Plugin bundle has the wrong identifier")
            if "HansJack" not in info.get("NSHumanReadableCopyright", ""):
                raise ValueError("Plugin bundle has the wrong copyright")

        root = Path(__file__).resolve().parent.parent
        source = f"{legal}/corresponding-source"
        for relative in ("LICENSE", "COPYRIGHT.txt", "licenses/OBS-THIRD-PARTY.txt", "licenses/GPL-2.0.txt"):
            expected = (root / relative).read_bytes()
            for prefix in (legal, source):
                if require(f"{prefix}/{relative}") != expected:
                    raise ValueError(f"Packaged notice differs from the build checkout: {relative}")
        for relative in ("obs-plugin/CMakeLists.txt", "obs-plugin/README.md", "obs-plugin/src/vsdirector-obs.c",
                         ".github/workflows/build-obs-plugin.yml", "tools/StageObsCompliance.ps1",
                         "tools/CheckObsPackage.py", "modinfo.json"):
            if require(f"{source}/{relative}") != (root / relative).read_bytes():
                raise ValueError(f"Packaged source differs from the build checkout: {relative}")
        for locale in (root / "obs-plugin/data/locale").glob("*.ini"):
            expected = locale.read_bytes()
            if require(f"{legal}/locale/{locale.name}") != expected:
                raise ValueError(f"Missing or outdated installed locale: {locale.name}")
            if require(f"{source}/obs-plugin/data/locale/{locale.name}") != expected:
                raise ValueError(f"Missing or outdated source locale: {locale.name}")
        require(f"{source}/obs-headers/libobs/obs-module.h")

        verified_archives = set()
        for target in targets:
            build = f"{source}/build-inputs/{target}"
            require(f"{build}/config/obsconfig.h")
            require(f"{build}/plugins-CMakeLists.txt")
            require(f"{build}/CMakePresets.json")
            manifest = json.loads(require(f"{build}/manifest.json"))
            if manifest["target"] != target or not manifest["directorRevision"]:
                raise ValueError(f"Wrong build provenance: {target}")
            if not manifest["sources"] or manifest["sources"][0]["path"] != ".":
                raise ValueError("OBS source archive is missing from the manifest")
            if manifest["sources"][0]["revision"] != manifest["obsRevision"]:
                raise ValueError("OBS source revision differs from the build manifest")
            for item in manifest["sources"]:
                contents = require(f"{source}/upstream/{item['archive']}")
                if hashlib.sha256(contents).hexdigest() != item["sha256"]:
                    raise ValueError(f"Source archive checksum mismatch: {item['archive']}")
                if item["archive"] not in verified_archives:
                    with tarfile.open(fileobj=io.BytesIO(contents), mode="r:gz") as upstream:
                        upstream_names = set(upstream.getnames())
                        if not upstream_names:
                            raise ValueError(f"Empty source archive: {item['archive']}")
                        if item["path"] == "." and not {"COPYING", "CMakeLists.txt", "libobs/obs.c"} <= upstream_names:
                            raise ValueError("OBS source archive lacks source/build/license files")
                    verified_archives.add(item["archive"])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    parser.add_argument("--platform", required=True, choices=("windows", "linux", "macos"))
    args = parser.parse_args()
    check_package(args.package, args.platform)
    print(f"PASS {args.platform}: install layout, architecture, notices and corresponding source")
