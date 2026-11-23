"""Packaging regression checks; synthetic binaries test layout, not OBS loading."""

import importlib.util
import io
import json
from pathlib import Path
import plistlib
import shutil
import struct
import subprocess
import tarfile
import tempfile
import unittest
import zipfile


ROOT = Path(__file__).resolve().parents[1]


class ObsPackagingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="vsdirector-obs-package-")
        cls.root = Path(cls.temp.name)
        for name in ("obs-plugin", "licenses", "tools", ".github/workflows"):
            shutil.copytree(ROOT / name, cls.root / name)
        for name in ("LICENSE", "COPYRIGHT.txt", "modinfo.json"):
            shutil.copy2(ROOT / name, cls.root / name)

        def git(directory, *args):
            return subprocess.check_output(
                ["git", "-c", "user.name=Packaging Test", "-c", "user.email=test@example.invalid",
                 "-c", "core.autocrlf=false", "-C", str(directory), *args], stderr=subprocess.PIPE,
                text=True, encoding="utf-8").strip()

        git(cls.root, "init")
        git(cls.root, "add", ".")
        git(cls.root, "commit", "-m", "Fixture")
        obs = cls.root / "obs-studio"
        obs.mkdir()
        for name, content in {
            "COPYING": "OBS license fixture", "CMakeLists.txt": "# OBS build fixture",
            "CMakePresets.json": "{\"version\": 8}",
            "libobs/obs.c": "/* OBS source fixture */", "libobs/obs-module.h": "/* OBS interface fixture */",
            "plugins/CMakeLists.txt": "# Plugin list fixture",
        }.items():
            path = obs / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content)
        git(obs, "init")
        git(obs, "add", ".")
        git(obs, "commit", "-m", "OBS fixture")
        # Exercise recursive source export rather than just an empty submodule list.
        dependency = cls.root / "dependency"
        dependency.mkdir()
        (dependency / "dependency.c").write_text("/* Dependency source fixture */")
        git(dependency, "init")
        git(dependency, "add", ".")
        git(dependency, "commit", "-m", "Dependency fixture")
        git(obs, "-c", "protocol.file.allow=always", "submodule", "add", str(dependency), "deps/fixture")
        git(obs, "commit", "-am", "Add dependency")
        cls.revision = git(obs, "rev-parse", "HEAD")
        config = obs / "build/config"
        config.mkdir(parents=True)
        (config / "obsconfig.h").write_text("#define OBS_VERSION 32\n")
        cls.stage = cls.root / "stage"
        cls.stage.mkdir()
        for target in ("windows-x64", "linux-x86_64", "macos-arm64", "macos-x86_64"):
            cls.run_stage(cls.stage / target, target, cls.revision, check=True)
        spec = importlib.util.spec_from_file_location("obs_package_check", cls.root / "tools/CheckObsPackage.py")
        cls.checker = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.checker)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    @classmethod
    def run_stage(cls, destination, target, revision, check=False):
        return subprocess.run(
            ["pwsh", "-NoProfile", "-File", str(cls.root / "tools/StageObsCompliance.ps1"),
             "-Destination", str(destination), "-ObsBuildDirectory", str(cls.root / "obs-studio/build"),
             "-BuildTarget", target, "-ExpectedObsRevision", revision],
            capture_output=True, text=True, encoding="utf-8", check=check)

    def contents(self, platform):
        target, legal, binary = {
            "windows": ("windows-x64", "data/obs-plugins/vsdirector-obs", "obs-plugins/64bit/vsdirector-obs.dll"),
            "linux": ("linux-x86_64", "vsdirector-obs/data", "vsdirector-obs/bin/64bit/vsdirector-obs.so"),
            "macos": ("macos-arm64", "vsdirector-obs.plugin/Contents/Resources", "vsdirector-obs.plugin/Contents/MacOS/vsdirector-obs"),
        }[platform]
        stage = self.stage / target
        files = {f"{legal}/{p.relative_to(stage).as_posix()}": p.read_bytes() for p in stage.rglob("*") if p.is_file()}
        for p in (self.root / "obs-plugin/data/locale").glob("*.ini"):
            files[f"{legal}/locale/{p.name}"] = p.read_bytes()
        data = bytearray(256)
        if platform == "windows":
            data[:2] = b"MZ"
            struct.pack_into("<I", data, 0x3C, 128)
            data[128:134] = b"PE\0\0\x64\x86"
        elif platform == "linux":
            data[:6] = b"\x7fELF\x02\x01"
            struct.pack_into("<H", data, 18, 62)
        else:
            struct.pack_into(">II", data, 0, 0xCAFEBABE, 2)
            struct.pack_into(">I", data, 8, 0x01000007)
            struct.pack_into(">I", data, 28, 0x0100000C)
            files["vsdirector-obs.plugin/Contents/Info.plist"] = plistlib.dumps({
                "CFBundleIdentifier": "studio.vscn.vsdirector-obs",
                "NSHumanReadableCopyright": "Copyright (c) 2026 HansJack (VSCN-Studio)",
            })
            intel = self.stage / "macos-x86_64/corresponding-source/build-inputs"
            files.update({f"{legal}/corresponding-source/build-inputs/{p.relative_to(intel).as_posix()}": p.read_bytes()
                          for p in intel.rglob("*") if p.is_file()})
        files[binary] = bytes(data)
        return files

    def check_archive(self, files, platform):
        path = self.root / ("package.tar.gz" if platform == "linux" else "package.zip")
        if platform == "linux":
            with tarfile.open(path, "w:gz") as archive:
                for name, data in files.items():
                    info = tarfile.TarInfo(name)
                    info.size = len(data)
                    archive.addfile(info, io.BytesIO(data))
        else:
            with zipfile.ZipFile(path, "w") as archive:
                for name, data in files.items():
                    archive.writestr(name, data)
        self.checker.check_package(path, platform)

    def test_all_platform_layouts(self):
        for platform in ("windows", "linux", "macos"):
            with self.subTest(platform=platform):
                self.check_archive(self.contents(platform), platform)

    def test_missing_workflow_and_generated_headers_rejected(self):
        for suffix in (".github/workflows/build-obs-plugin.yml", "config/obsconfig.h"):
            with self.subTest(suffix=suffix):
                files = {k: v for k, v in self.contents("macos").items() if not k.endswith(suffix)}
                with self.assertRaisesRegex(ValueError, "Missing package file"):
                    self.check_archive(files, "macos")

    def test_changed_license_rejected(self):
        files = self.contents("windows")
        files["data/obs-plugins/vsdirector-obs/LICENSE"] = b"stale copyright"
        with self.assertRaisesRegex(ValueError, "notice differs"):
            self.check_archive(files, "windows")

    def test_damaged_upstream_archive_rejected(self):
        files = self.contents("linux")
        files[next(k for k in files if k.endswith("upstream/obs-studio.tar.gz"))] += b"changed"
        with self.assertRaisesRegex(ValueError, "checksum mismatch"):
            self.check_archive(files, "linux")

    def test_submodule_sources_included(self):
        source = self.stage / "windows-x64/corresponding-source"
        manifest = json.loads((source / "build-inputs/windows-x64/manifest.json").read_text(encoding="utf-8-sig"))
        module = next(item for item in manifest["sources"] if item["path"] == "deps/fixture")
        with tarfile.open(source / "upstream" / module["archive"], "r:gz") as archive:
            self.assertIn("dependency.c", archive.getnames())

    def test_wrong_architecture_rejected(self):
        files = self.contents("windows")
        key = "obs-plugins/64bit/vsdirector-obs.dll"
        files[key] = files[key].replace(b"PE\0\0\x64\x86", b"PE\0\0\x4c\x01")
        with self.assertRaisesRegex(ValueError, "Windows x64"):
            self.check_archive(files, "windows")

    def test_incomplete_macos_slices_rejected(self):
        files = {k: v for k, v in self.contents("macos").items() if "/build-inputs/macos-x86_64/" not in k}
        with self.assertRaisesRegex(ValueError, "Missing package file"):
            self.check_archive(files, "macos")

    def test_staging_wrong_revision_and_missing_config_fail(self):
        result = self.run_stage(self.root / "wrong", "windows-x64", "0" * 40)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("does not match", result.stderr)
        config = self.root / "obs-studio/build/config/obsconfig.h"
        saved = config.read_bytes()
        try:
            config.unlink()
            result = self.run_stage(self.root / "missing", "windows-x64", self.revision)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("obsconfig.h", result.stderr)
        finally:
            config.write_bytes(saved)


if __name__ == "__main__":
    unittest.main()
