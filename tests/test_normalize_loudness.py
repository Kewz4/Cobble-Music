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

    def test_known_short_stinger_layout_is_paired(self) -> None:
        self.assertEqual(
            "assets/cobblemon/sounds/battle/special/pla/origin.ogg",
            NORMALIZE.paired_main(
                "assets/cobblemon/sounds/battle/special/pla/origin_intro.ogg"
            ),
        )

    def test_non_battle_intro_is_not_grouped(self) -> None:
        self.assertIsNone(NORMALIZE.paired_main("music/route_intro.ogg"))

    def test_missing_main_is_rejected(self) -> None:
        with self.assertRaisesRegex(RuntimeError, "no matching main theme"):
            NORMALIZE.pairing_summary(
                [track("assets/cobblemon/sounds/battle/special/bdsp/arceus_intro.ogg")]
            )


class VerificationTests(unittest.TestCase):
    def test_peak_repair_candidates_are_only_vorbis_outliers(self) -> None:
        safe = track("assets/cobblemon/sounds/battle/safe.ogg", -16.0, -1.01)
        repair = track("assets/cobblemon/sounds/battle/repair.ogg", -16.0, -0.99)
        self.assertEqual(
            [repair], NORMALIZE.peak_repair_candidates([safe, repair], -1.5)
        )
        unsupported = track("music/repair.mp3", -16.0, -0.99)
        with self.assertRaisesRegex(RuntimeError, "restricted to verified Vorbis"):
            NORMALIZE.peak_repair_candidates([unsupported], -1.5)

    def test_peak_limited_shared_group_can_remain_below_target(self) -> None:
        main = "assets/cobblemon/sounds/battle/special/main.ogg"
        before = track(main, -20.0, -1.0)
        after = track(main, -18.0, -3.0)
        method = {
            "method": "sharedBattleSegmentGain",
            "main": main,
            "members": [main],
            "appliedGainDb": 2.0,
            "peakLimited": True,
        }
        result = NORMALIZE.verify_outputs(
            [before], [after], {main: method}, -16.0, -1.5
        )
        self.assertEqual(result["peakLimitedPairedMains"], 1)
        self.assertEqual(result["maximumPeakLimitedPairedMainTargetErrorLu"], 2.0)

    def test_dynamic_loudnorm_may_finish_quieter_to_preserve_peak_limit(self) -> None:
        path = "music/dynamic.mp3"
        before = track(path, -24.0, -8.0)
        before["normalizationType"] = "dynamic"
        after = track(path, -17.49, -1.49)
        result = NORMALIZE.verify_outputs(
            [before],
            [after],
            {path: {"method": "measuredTwoPassLoudnorm"}},
            -16.0,
            -1.5,
        )
        self.assertEqual(result["dynamicConstrainedStandaloneTracks"], 1)
        self.assertEqual(result["maximumDynamicStandaloneTargetErrorLu"], 1.49)
        after["input_i"] = -17.51
        with self.assertRaisesRegex(RuntimeError, "missed target"):
            NORMALIZE.verify_outputs(
                [before],
                [after],
                {path: {"method": "measuredTwoPassLoudnorm"}},
                -16.0,
                -1.5,
            )

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

    def test_existing_output_records_require_every_completed_audio_file(self) -> None:
        relative = "music/test.mp3"
        methods = {relative: {"method": "measuredTwoPassLoudnorm"}}
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            destination = root / relative
            destination.parent.mkdir(parents=True)
            with self.assertRaisesRegex(RuntimeError, "missing or empty"):
                NORMALIZE.existing_output_records(root, [track(relative)], methods)
            destination.write_bytes(b"normalized-audio")
            records = NORMALIZE.existing_output_records(root, [track(relative)], methods)
            self.assertEqual(records[0]["size"], len(b"normalized-audio"))
            self.assertEqual(records[0]["method"], "measuredTwoPassLoudnorm")

    def test_analysis_tree_validation_rejects_post_report_tampering(self) -> None:
        relative = "music/test.mp3"
        expected_target = (-16.0, -1.5, 11.0)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            audio = root / relative
            audio.parent.mkdir(parents=True)
            audio.write_bytes(b"first")
            report_track = track(relative)
            report_track.update(
                {
                    "size": audio.stat().st_size,
                    "sha256": NORMALIZE.sha256(audio),
                }
            )
            analysis = {
                "ffmpegSha256": "abc123",
                "target": {
                    "integratedLufs": -16.0,
                    "truePeakDbtp": -1.5,
                    "loudnessRangeLu": 11.0,
                },
                "tracks": [report_track],
            }
            validated = NORMALIZE.validate_analysis_for_tree(
                analysis, root, [audio], "abc123", expected_target, "Fixture"
            )
            self.assertEqual(validated[0]["path"], relative)
            audio.write_bytes(b"tampered")
            with self.assertRaisesRegex(RuntimeError, "changed after analysis"):
                NORMALIZE.validate_analysis_for_tree(
                    analysis, root, [audio], "abc123", expected_target, "Fixture"
                )


if __name__ == "__main__":
    unittest.main()
