from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "normalize_loudness", ROOT / "tools" / "Normalize-CobbleMusicLoudness.py"
)
assert SPEC is not None and SPEC.loader is not None
NORMALIZE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(NORMALIZE)


def track(path: str, loudness: float = -20.0, peak: float = -4.0) -> dict[str, object]:
    return {
        "path": path,
        "input_i": loudness,
        "input_tp": peak,
        "input_lra": 8.0,
        "sampleRate": 48000,
        "channelLayout": "stereo",
        "durationSeconds": 120.0,
    }


class PairingTests(unittest.TestCase):
    def test_legacy_intro_layout(self) -> None:
        self.assertEqual(
            NORMALIZE.paired_main(
                "assets/cobblemon/sounds/battle/legacy/game/intro/wild-intro.ogg"
            ),
            "assets/cobblemon/sounds/battle/legacy/game/wild.ogg",
        )

    def test_legacy_loop_layout(self) -> None:
        self.assertEqual(
            NORMALIZE.paired_main(
                "assets/cobblemon/sounds/battle/legacy/game/intro/sunset-loop.ogg"
            ),
            "assets/cobblemon/sounds/battle/legacy/game/sunset.ogg",
        )

    def test_new_sibling_intro_layout(self) -> None:
        self.assertEqual(
            NORMALIZE.paired_main(
                "assets/cobblemon/sounds/battle/special/bdsp/arceus_intro.ogg"
            ),
            "assets/cobblemon/sounds/battle/special/bdsp/arceus.ogg",
        )

    def test_non_battle_intro_is_not_grouped(self) -> None:
        self.assertIsNone(NORMALIZE.paired_main("music/route_intro.ogg"))

    def test_missing_main_is_rejected(self) -> None:
        with self.assertRaisesRegex(RuntimeError, "no matching main theme"):
            NORMALIZE.pairing_summary(
                [track("assets/cobblemon/sounds/battle/special/bdsp/arceus_intro.ogg")]
            )


class VerificationTests(unittest.TestCase):
    def test_shared_group_main_must_reach_target(self) -> None:
        main = "assets/cobblemon/sounds/battle/special/bdsp/arceus.ogg"
        intro = "assets/cobblemon/sounds/battle/special/bdsp/arceus_intro.ogg"
        inputs = [track(main, -20.0), track(intro, -19.0)]
        outputs = [track(main, -18.0), track(intro, -17.0)]
        method = {
            "method": "sharedBattleSegmentGain",
            "main": main,
            "members": [main, intro],
            "appliedGainDb": 2.0,
        }
        with self.assertRaisesRegex(RuntimeError, "missed loudness target"):
            NORMALIZE.verify_outputs(
                inputs, outputs, {main: method, intro: method}, -16.0, -1.5
            )

    def test_battle_runtime_volumes_are_uniform_and_only_approved_values_change(self) -> None:
        registry = {
            "battle.one": {
                "sounds": [
                    {"name": "cobblemon:battle/wild/one", "volume": 0.8},
                    {"name": "cobblemon:battle/wild/two"},
                ]
            },
            "not.music": {
                "sounds": [{"name": "cobblemon:ui/click", "volume": 0.4}]
            },
        }
        with tempfile.TemporaryDirectory() as temporary:
            temporary_root = Path(temporary)
            source = temporary_root / "source"
            output = temporary_root / "output"
            for root in (source, output):
                path = root / NORMALIZE.BATTLE_SOUNDS_PATH
                path.parent.mkdir(parents=True)
                path.write_text(json.dumps(registry), encoding="utf-8")
            summary = NORMALIZE.set_battle_event_volumes(source, output, 2)
            NORMALIZE.verify_battle_event_volumes(source, output, summary)
            transformed = json.loads(
                (output / NORMALIZE.BATTLE_SOUNDS_PATH).read_text(encoding="utf-8")
            )
            self.assertEqual(summary["references"], 2)
            self.assertEqual(
                [item["volume"] for item in transformed["battle.one"]["sounds"]],
                [1.0, 1.0],
            )
            self.assertEqual(transformed["not.music"], registry["not.music"])

    def test_channel_layout_change_is_rejected(self) -> None:
        path = "music/test.mp3"
        before = track(path, -16.0)
        after = track(path, -16.0)
        after["channelLayout"] = "mono"
        with self.assertRaisesRegex(RuntimeError, "channel layout changed"):
            NORMALIZE.verify_outputs(
                [before],
                [after],
                {path: {"method": "measuredTwoPassLoudnorm"}},
                -16.0,
                -1.5,
            )


if __name__ == "__main__":
    unittest.main()
