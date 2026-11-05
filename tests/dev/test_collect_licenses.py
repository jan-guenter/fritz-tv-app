"""Release notices must include the shipped runtime and reject unsafe metadata."""

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location(
    "collect_licenses", Path(__file__).resolve().parents[2] / "packaging/collect-licenses.py"
)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("Cannot load the dependency notice collector")
COLLECTOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(COLLECTOR)


class NoticeCollectionTests(unittest.TestCase):
    def test_dtd_is_rejected_before_entity_expansion(self):
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory)
            (package / "unsafe.nuspec").write_text(
                '<!DOCTYPE package [<!ENTITY secret SYSTEM "file:///unavailable-secret">]>'
                "<package><metadata><id>&secret;</id></metadata></package>",
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ValueError, "DTD"):
                COLLECTOR.metadata(package)

    def test_oversized_manifest_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory)
            (package / "large.nuspec").write_bytes(b" " * (512 * 1024 + 1))
            with self.assertRaisesRegex(ValueError, "bounded"):
                COLLECTOR.metadata(package)

    def test_licence_cannot_escape_restored_package(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            package = root / "cache/example/1.0.0"
            package.mkdir(parents=True)
            (package.parent / "private.txt").write_text("private configuration", encoding="utf-8")
            self.write_manifest(package, "../private.txt")
            assets = self.write_assets(root, "example/1.0.0", [])
            with self.assertRaisesRegex(ValueError, "escapes package"):
                COLLECTOR.collect(assets, root / "notices", "linux-x64")
            self.assertFalse((root / "notices/example/1.0.0/LICENSE.txt").exists())

    def test_runtime_licence_and_notices_are_retained_for_selected_platform(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            package = root / "cache/example/1.0.0"
            runtime = root / "cache/microsoft.netcore.app.runtime.linux-x64/10.0.14"
            for path in (package, runtime):
                path.mkdir(parents=True)
                self.write_manifest(path, "LICENSE.TXT")
                (path / "LICENSE.TXT").write_text("Synthetic licence fixture", encoding="utf-8")
            (runtime / "THIRD-PARTY-NOTICES.TXT").write_text("Synthetic runtime attribution", encoding="utf-8")
            downloads = [
                {"name": "Microsoft.NETCore.App.Runtime.linux-x64", "version": "[10.0.14, 10.0.14]"},
                {"name": "Microsoft.NETCore.App.Runtime.win-x64", "version": "[10.0.14, 10.0.14]"},
            ]
            assets = self.write_assets(root, "example/1.0.0", downloads)
            output = root / "notices"
            COLLECTOR.collect(assets, output, "linux-x64")
            inventory = json.loads((output / "packages.json").read_text(encoding="utf-8"))
            self.assertEqual(
                {"example", "Microsoft.NETCore.App.Runtime.linux-x64"}, {item["package"] for item in inventory}
            )
            self.assertEqual(
                "Synthetic runtime attribution",
                (output / "Microsoft.NETCore.App.Runtime.linux-x64/10.0.14/THIRD-PARTY-NOTICES.TXT").read_text(
                    encoding="utf-8"
                ),
            )

    @staticmethod
    def write_manifest(package: Path, licence: str) -> None:
        (package / "example.nuspec").write_text(
            f'<package><metadata><id>example</id><license type="file">{licence}</license></metadata></package>',
            encoding="utf-8",
        )

    @staticmethod
    def write_assets(root: Path, package: str, downloads: list[dict[str, str]]) -> Path:
        assets = root / "assets.json"
        assets.write_text(
            json.dumps(
                {
                    "packageFolders": {str(root / "cache"): {}},
                    "libraries": {package: {"type": "package", "path": package}},
                    "project": {"frameworks": {"net10.0": {"downloadDependencies": downloads}}},
                }
            ),
            encoding="utf-8",
        )
        return assets
