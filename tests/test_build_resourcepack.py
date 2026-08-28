from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "build_resourcepack", ROOT / "tools" / "Build-CobbleMusicResourcePack.py"
)
assert SPEC is not None and SPEC.loader is not None
BUILDER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BUILDER)


class ResourcePackBuilderTests(unittest.TestCase):
    def test_build_is_deterministic_zip64_safe_and_codec_aware(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source"
            (source / "assets" / "test").mkdir(parents=True)
            (source / "pack.mcmeta").write_text(
                json.dumps({"pack": {"pack_format": 34, "description": "fixture"}}),
                encoding="utf-8",
            )
            (source / "assets" / "test" / "song.ogg").write_bytes(b"audio-fixture")

            first = root / "first.zip"
            second = root / "second.zip"
            first_result = BUILDER.build(source, first, root / "first.json")
            second_result = BUILDER.build(source, second, root / "second.json")
            self.assertEqual(first_result["archiveSha256"], second_result["archiveSha256"])
            self.assertEqual(first.read_bytes(), second.read_bytes())

            with zipfile.ZipFile(first) as archive:
                self.assertIsNone(archive.testzip())
                self.assertEqual(
                    archive.namelist(), ["assets/test/song.ogg", "pack.mcmeta"]
                )
                self.assertEqual(
                    archive.getinfo("assets/test/song.ogg").compress_type,
                    zipfile.ZIP_STORED,
                )
                self.assertEqual(
                    archive.getinfo("pack.mcmeta").compress_type,
                    zipfile.ZIP_DEFLATED,
                )
                self.assertTrue(
                    all(info.date_time == BUILDER.FIXED_ZIP_TIME for info in archive.infolist())
                )


if __name__ == "__main__":
    unittest.main()
