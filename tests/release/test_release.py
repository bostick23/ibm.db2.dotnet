"""Exercise release gates without a NuGet account or an IBM i connection."""

import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
import zipfile

SCRIPT = Path(__file__).resolve().parents[2] / ".github/scripts/release.py"
SPEC = importlib.util.spec_from_file_location("release", SCRIPT)
release = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(release)


class ReleaseTests(unittest.TestCase):
    def test_accepts_matching_stable_and_prerelease_versions(self):
        release.validate_version("v1.2.3", "1.2.3", False)
        release.validate_version("v0.5.0-alpha.4", "0.5.0-alpha.4", True)

    def test_rejects_wrong_tags_versions_and_prerelease_flags(self):
        for tag, version, prerelease in (
            ("0.5.0-alpha.4", "0.5.0-alpha.4", True),
            ("v0.5.0-alpha.3", "0.5.0-alpha.4", True),
            ("v0.5.0-alpha.4", "0.5.0-alpha.4", False),
            ("v1.2.3", "1.2.3", True),
            ("v01.2.3", "01.2.3", False),
            ("v1.2.3-alpha.01", "1.2.3-alpha.01", True),
            ("v1.2.3+build", "1.2.3+build", False),
        ):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                release.validate_version(tag, version, prerelease)

    def test_rejects_release_commit_outside_main_and_moved_tags(self):
        with tempfile.TemporaryDirectory() as directory:
            def git(*arguments):
                return subprocess.run(["git", *arguments], cwd=directory, text=True,
                                      capture_output=True, check=True).stdout.strip()

            git("init", "--initial-branch=main")
            git("config", "user.name", "Release test")
            git("config", "user.email", "release@example.invalid")
            git("commit", "--allow-empty", "-m", "main")
            main = git("rev-parse", "HEAD")
            git("update-ref", "refs/remotes/origin/main", main)
            git("tag", "v1.2.3")
            self.assertEqual(main, release.validate_git("v1.2.3", cwd=directory))
            git("switch", "-c", "unmerged")
            git("commit", "--allow-empty", "-m", "unmerged")
            git("tag", "v1.2.4")
            with self.assertRaisesRegex(ValueError, "history of main"):
                release.validate_git("v1.2.4", cwd=directory)
            with self.assertRaisesRegex(ValueError, "checked-out commit"):
                release.validate_git("v1.2.3", cwd=directory)

    def test_checks_both_packages_metadata_contents_and_source_link(self):
        self.check_packages()

    def test_rejects_wrong_package_id_version_commit_or_missing_target(self):
        for metadata in (
            {"package_id": "Other.Package"}, {"version": "0.5.0-alpha.3"},
            {"commit": "b" * 40}, {"omit": "lib/net10.0/Db2i.dll"},
            {"omit": "LICENSE.md"},
        ):
            with self.subTest(metadata=metadata), self.assertRaises(ValueError):
                self.check_packages(**metadata)

    def test_rejects_missing_or_incorrect_source_link(self):
        with self.assertRaisesRegex(ValueError, "Source Link"):
            self.check_packages(source_commit="b" * 40)

    def test_rejects_unexpected_package_files(self):
        with self.assertRaisesRegex(ValueError, "exactly one"):
            self.check_packages(extra=True)

    def check_packages(self, package_id="Db2i.AdoNet", version="0.5.0-alpha.4",
                       commit="a" * 40, omit=None, source_commit="a" * 40, extra=False):
        manifest = f'''<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata><id>{package_id}</id><version>{version}</version>
          <readme>README.md</readme><license type="expression">IPL-1.0</license>
          <repository type="git" url="{release.REPOSITORY}" commit="{commit}" /></metadata></package>'''
        with tempfile.TemporaryDirectory() as directory:
            for extension in ("nupkg", "snupkg"):
                name = f"Db2i.AdoNet.0.5.0-alpha.4.{extension}"
                with zipfile.ZipFile(Path(directory) / name, "w") as package:
                    package.writestr("Db2i.AdoNet.nuspec", manifest)
                    contents = ("README.md", "LICENSE.md", "NOTICE.md",
                                "lib/net8.0/Db2i.dll", "lib/net10.0/Db2i.dll",
                                "lib/net8.0/Db2i.xml", "lib/net10.0/Db2i.xml") if extension == "nupkg" else (
                                    "lib/net8.0/Db2i.pdb", "lib/net10.0/Db2i.pdb")
                    for content in contents:
                        if content != omit:
                            package.writestr(content, f"https://raw.githubusercontent.com/bostick23/ibm.db2.dotnet/{source_commit}/*")
            if extra:
                Path(directory, "unexpected.nupkg").touch()
            checksums = release.validate_packages(directory, "0.5.0-alpha.4", "a" * 40)
            self.assertEqual(2, len(checksums.splitlines()))


if __name__ == "__main__":
    unittest.main()
