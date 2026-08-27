from __future__ import annotations

import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "build_cobble_music_mrpack", ROOT / "tools" / "Build-CobbleMusicMrpack.py"
)
assert SPEC and SPEC.loader
MRPACK = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MRPACK)


def write_jar(path: Path, environment: str = "*") -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr(
            "fabric.mod.json",
            json.dumps({"schemaVersion": 1, "id": path.stem.lower(), "version": "1", "environment": environment}),
        )


class MrpackBuilderTests(unittest.TestCase):
    def test_path_and_exclusion_policy(self) -> None:
        self.assertEqual("config/example.json", MRPACK.canonical_relative("config/example.json"))
        with self.assertRaises(RuntimeError):
            MRPACK.canonical_relative("../outside.json")
        self.assertTrue(MRPACK.should_exclude_override("config/MCBrowser/tabs.json"))
        self.assertTrue(MRPACK.should_exclude_override("config/mod/cache/runtime.json"))
        self.assertTrue(MRPACK.should_exclude_override("config/example.json.bak-20260822"))
        self.assertFalse(MRPACK.should_exclude_override("config/asyncparticles/asyncparticles.json"))

    def test_tiny_pack_build(self) -> None:
        with tempfile.TemporaryDirectory(prefix="cobble-mrpack-test-") as temporary:
            root = Path(temporary)
            instance = root / "instance"
            minecraft = instance / "minecraft"
            metadata_root = minecraft / "mods" / ".index"
            metadata_root.mkdir(parents=True)
            (instance / "mmc-pack.json").write_text(
                json.dumps(
                    {
                        "components": [
                            {"uid": "net.minecraft", "version": "1.21.1"},
                            {"uid": "net.fabricmc.fabric-loader", "version": "0.19.3"},
                        ]
                    }
                ),
                encoding="utf-8",
            )

            hosted = minecraft / "mods" / "hosted.jar"
            custom = minecraft / "mods" / "custom.jar"
            axiom = minecraft / "mods" / "Axiom-local.jar"
            write_jar(hosted, "client")
            write_jar(custom)
            write_jar(axiom, "client")
            sha512 = hashlib.sha512(hosted.read_bytes()).hexdigest()
            (metadata_root / "hosted.pw.toml").write_text(
                "\n".join(
                    [
                        "filename = 'hosted.jar'",
                        "name = 'Hosted'",
                        "side = ''",
                        "[download]",
                        f"hash = '{sha512}'",
                        "hash-format = 'sha512'",
                        "mode = 'url'",
                        "url = 'https://cdn.modrinth.com/data/example/versions/one/hosted.jar'",
                        "[update.modrinth]",
                        "mod-id = 'example'",
                        "version = 'one'",
                        "",
                    ]
                ),
                encoding="utf-8",
            )

            config = minecraft / "config"
            config.mkdir()
            (config / "cobble-music-pack-version.json").write_text(
                json.dumps({"pack": "Kewz's Cobblemon", "version": "1.0.6", "channel": "stable"}),
                encoding="utf-8",
            )
            (config / "ReactiveMusic.json5").write_text("live-player-state", encoding="utf-8")
            (config / "example.json.bak").write_text("backup", encoding="utf-8")
            (minecraft / "options.txt").write_text("key_key.jump:key.keyboard.space\n", encoding="utf-8")
            templates = root / "templates" / "config"
            templates.mkdir(parents=True)
            (templates / "ReactiveMusic.json5").write_text("sanitized-default", encoding="utf-8")
            (templates / "iris.properties").write_text("shaderPack=Example\n", encoding="utf-8")

            output = root / "client.mrpack"
            args = type(
                "Args",
                (),
                {
                    "instance": instance,
                    "version": "1.0.6",
                    "output": output,
                    "template_root": root / "templates",
                },
            )()
            report = MRPACK.build(args)
            self.assertEqual(1, report["downloadableMods"])
            self.assertEqual(1, report["bundledMods"])
            self.assertEqual(["Axiom-local.jar"], report["optionalOrDisabledModsNotBundled"])

            with zipfile.ZipFile(output) as archive:
                index = json.loads(archive.read("modrinth.index.json"))
                self.assertEqual("required", index["files"][0]["env"]["client"])
                self.assertEqual("unsupported", index["files"][0]["env"]["server"])
                self.assertEqual(b"sanitized-default", archive.read("overrides/config/ReactiveMusic.json5"))
                self.assertEqual(custom.read_bytes(), archive.read("overrides/mods/custom.jar"))
                self.assertNotIn("overrides/mods/Axiom-local.jar", archive.namelist())
                self.assertNotIn("overrides/config/example.json.bak", archive.namelist())


if __name__ == "__main__":
    unittest.main()
