#!/usr/bin/env python3
"""Measure and normalize every music file in the Cobble Music mega pack.

The tool never edits the source tree.  It uses FFmpeg's EBU R128/ITU-R
BS.1770 loudnorm implementation, preserves each input sample rate, and writes
an auditable JSON report.  Long tracks use measured two-pass normalization.
Battle intro/loop segments and their matching main theme receive one shared
constant gain so a stitched battle cue cannot jump at the boundary.  This
covers both legacy ``intro/name-intro.ogg`` layouts and the pack's newer
``name_intro.ogg`` sibling layout.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import hashlib
import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import threading
from typing import Any, Iterable, NoReturn


AUDIO_EXTENSIONS = {".mp3", ".ogg"}
BATTLE_SOUNDS_PATH = Path("assets/cobblemon/sounds.json")
LOUDNORM_JSON = re.compile(r'\{\s*"input_i"\s*:.*?\}', re.DOTALL)
DURATION = re.compile(r"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)")
AUDIO_STREAM = re.compile(r"Stream #.*?Audio:.*?,\s*(\d+)\s*Hz,\s*([^,]+)")
CREATE_NO_WINDOW = 0x08000000 if os.name == "nt" else 0


def fail(message: str) -> NoReturn:
    raise RuntimeError(message)


def canonical_root(path: str, *, must_exist: bool) -> Path:
    result = Path(path).expanduser().resolve()
    if must_exist and not result.is_dir():
        fail(f"Directory does not exist: {result}")
    return result


def ensure_plain_tree(root: Path) -> None:
    is_junction = getattr(root, "is_junction", lambda: False)
    if root.is_symlink() or is_junction():
        fail(f"Symbolic links and junctions are not accepted in the pack tree: {root}")
    for directory, directory_names, file_names in os.walk(root):
        directory_path = Path(directory)
        directory_is_junction = getattr(directory_path, "is_junction", lambda: False)
        if directory_path.is_symlink() or directory_is_junction():
            fail(f"Symbolic links and junctions are not accepted in the pack tree: {directory_path}")
        for name in [*directory_names, *file_names]:
            candidate = directory_path / name
            candidate_is_junction = getattr(candidate, "is_junction", lambda: False)
            if candidate.is_symlink() or candidate_is_junction():
                fail(f"Symbolic links and junctions are not accepted in the pack tree: {candidate}")


def discover_audio(root: Path) -> list[Path]:
    files = [
        path
        for path in root.rglob("*")
        if path.is_file() and path.suffix.lower() in AUDIO_EXTENSIONS
    ]
    files.sort(key=lambda path: path.relative_to(root).as_posix().casefold())
    if not files:
        fail(f"No MP3 or OGG music files were found under {root}")
    folded: dict[str, str] = {}
    for path in files:
        relative = path.relative_to(root).as_posix()
        key = relative.casefold()
        if key in folded:
            fail(f"Case-colliding music paths are unsafe: {folded[key]} and {relative}")
        folded[key] = relative
    return files


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while chunk := handle.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def run_ffmpeg(ffmpeg: Path, arguments: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(ffmpeg), *arguments],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        creationflags=CREATE_NO_WINDOW,
        check=False,
    )


def number(value: Any) -> float | None:
    try:
        parsed = float(str(value))
    except (TypeError, ValueError):
        return None
    return parsed if math.isfinite(parsed) else None


def parse_duration(text: str) -> float | None:
    match = DURATION.search(text)
    if not match:
        return None
    hours, minutes, seconds = match.groups()
    return int(hours) * 3600 + int(minutes) * 60 + float(seconds)


def parse_stream(text: str) -> tuple[int | None, str | None]:
    match = AUDIO_STREAM.search(text)
    if not match:
        return None, None
    return int(match.group(1)), match.group(2).strip()


def analyze_one(
    ffmpeg: Path,
    source_root: Path,
    path: Path,
    target_lufs: float,
    true_peak: float,
    target_lra: float,
) -> dict[str, Any]:
    relative = path.relative_to(source_root).as_posix()
    filter_value = (
        f"loudnorm=I={target_lufs:.2f}:TP={true_peak:.2f}:"
        f"LRA={target_lra:.2f}:print_format=json"
    )
    process = run_ffmpeg(
        ffmpeg,
        [
            "-hide_banner",
            "-nostdin",
            "-i",
            str(path),
            "-map",
            "0:a:0",
            "-af",
            filter_value,
            "-f",
            "null",
            "NUL" if os.name == "nt" else "/dev/null",
        ],
    )
    combined = f"{process.stdout}\n{process.stderr}"
    matches = LOUDNORM_JSON.findall(combined)
    if process.returncode != 0 or not matches:
        tail = "\n".join(combined.splitlines()[-20:])
        fail(f"FFmpeg loudness analysis failed for {relative}:\n{tail}")
    raw = json.loads(matches[-1])
    sample_rate, channel_layout = parse_stream(combined)
    duration = parse_duration(combined)
    required = {
        "input_i": number(raw.get("input_i")),
        "input_tp": number(raw.get("input_tp")),
        "input_lra": number(raw.get("input_lra")),
        "input_thresh": number(raw.get("input_thresh")),
        "target_offset": number(raw.get("target_offset")),
    }
    missing = [name for name, value in required.items() if value is None]
    # FFmpeg reports -inf integrated loudness and no target offset for a few
    # sub-second transition stingers. They are safe to admit only when the
    # path deterministically pairs with a measurable battle main theme; that
    # group is normalized with one shared gain and verified by true-peak delta.
    allowed_unmeasurable_segment = (
        paired_main(relative) is not None
        and set(missing).issubset({"input_i", "target_offset"})
    )
    if (missing and not allowed_unmeasurable_segment) or sample_rate is None or duration is None:
        fail(f"Incomplete loudness/stream analysis for {relative}: {', '.join(missing)}")
    return {
        "path": relative,
        "extension": path.suffix.lower(),
        "size": path.stat().st_size,
        "sha256": sha256(path),
        "durationSeconds": round(duration, 6),
        "sampleRate": sample_rate,
        "channelLayout": channel_layout,
        **required,
        "firstPassOutputI": number(raw.get("output_i")),
        "firstPassOutputTp": number(raw.get("output_tp")),
        "firstPassOutputLra": number(raw.get("output_lra")),
        "normalizationType": raw.get("normalization_type"),
    }


def percentile(values: list[float], fraction: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    if len(ordered) == 1:
        return ordered[0]
    position = (len(ordered) - 1) * fraction
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return ordered[lower]
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def metric_summary(tracks: Iterable[dict[str, Any]]) -> dict[str, Any]:
    selected = list(tracks)
    loudness = [float(track["input_i"]) for track in selected if track.get("input_i") is not None]
    peaks = [float(track["input_tp"]) for track in selected if track.get("input_tp") is not None]
    durations = [float(track["durationSeconds"]) for track in selected]
    return {
        "count": len(selected),
        "durationHours": round(sum(durations) / 3600, 4),
        "integratedLufs": {
            "minimum": round(min(loudness), 3) if loudness else None,
            "p05": round(percentile(loudness, 0.05), 3) if loudness else None,
            "median": round(percentile(loudness, 0.50), 3) if loudness else None,
            "p95": round(percentile(loudness, 0.95), 3) if loudness else None,
            "maximum": round(max(loudness), 3) if loudness else None,
            "spread": round(max(loudness) - min(loudness), 3) if loudness else None,
        },
        "truePeakDbtp": {
            "maximum": round(max(peaks), 3) if peaks else None,
            "median": round(percentile(peaks, 0.50), 3) if peaks else None,
        },
    }


def summarize(tracks: list[dict[str, Any]]) -> dict[str, Any]:
    categories: dict[str, list[dict[str, Any]]] = {
        "reactiveMp3": [],
        "cobblemonBattleOgg": [],
        "lumymonOgg": [],
        "other": [],
    }
    for track in tracks:
        path = str(track["path"])
        if path.startswith("music/") and path.endswith(".mp3"):
            categories["reactiveMp3"].append(track)
        elif path.startswith("assets/cobblemon/sounds/battle/"):
            categories["cobblemonBattleOgg"].append(track)
        elif path.startswith("assets/lumymon/sounds/music/"):
            categories["lumymonOgg"].append(track)
        else:
            categories["other"].append(track)
    return {
        "all": metric_summary(tracks),
        "categories": {name: metric_summary(items) for name, items in categories.items()},
    }


def analyze_all(
    ffmpeg: Path,
    source_root: Path,
    files: list[Path],
    jobs: int,
    target_lufs: float,
    true_peak: float,
    target_lra: float,
) -> list[dict[str, Any]]:
    print(f"Analyzing {len(files)} tracks with {jobs} FFmpeg workers...", flush=True)
    results: list[dict[str, Any]] = []
    errors: list[str] = []
    lock = threading.Lock()

    def work(path: Path) -> dict[str, Any]:
        return analyze_one(ffmpeg, source_root, path, target_lufs, true_peak, target_lra)

    with concurrent.futures.ThreadPoolExecutor(max_workers=jobs) as executor:
        futures = {executor.submit(work, path): path for path in files}
        for index, future in enumerate(concurrent.futures.as_completed(futures), 1):
            path = futures[future]
            try:
                result = future.result()
                with lock:
                    results.append(result)
            except Exception as exc:  # collect all bad files, then fail once
                errors.append(f"{path.relative_to(source_root).as_posix()}: {exc}")
            if index % 25 == 0 or index == len(files):
                print(f"  analyzed {index}/{len(files)}", flush=True)
    if errors:
        fail("Loudness analysis failed:\n" + "\n".join(errors))
    results.sort(key=lambda item: str(item["path"]).casefold())
    return results


def paired_main(relative: str) -> str | None:
    path = Path(relative)
    if path.suffix.lower() != ".ogg":
        return None
    normalized = path.as_posix().casefold()
    if not normalized.startswith("assets/cobblemon/sounds/battle/"):
        return None
    stem = path.stem
    lowered = stem.casefold()
    if path.parent.name.casefold() == "intro":
        if lowered.endswith("-intro"):
            stem = stem[: -len("-intro")]
        elif lowered.endswith("-loop"):
            stem = stem[: -len("-loop")]
        else:
            return None
        return (path.parent.parent / f"{stem}.ogg").as_posix()
    if lowered.endswith("_intro"):
        stem = stem[: -len("_intro")]
        return (path.parent / f"{stem}.ogg").as_posix()
    return None


def pairing_summary(tracks: list[dict[str, Any]]) -> dict[str, Any]:
    paths = {str(track["path"]) for track in tracks}
    pairs: list[dict[str, str]] = []
    for segment in sorted(paths, key=str.casefold):
        main = paired_main(segment)
        if main is None:
            continue
        if main not in paths:
            fail(f"Battle segment has no matching main theme: {segment} -> {main}")
        pairs.append({"segment": segment, "main": main})
    return {
        "pairedSegments": len(pairs),
        "pairedMainThemes": len({pair["main"] for pair in pairs}),
        "pairs": pairs,
    }


def build_methods(
    tracks: list[dict[str, Any]], target_lufs: float, true_peak: float
) -> dict[str, dict[str, Any]]:
    by_path = {str(track["path"]): track for track in tracks}
    segments_by_main: dict[str, list[str]] = {}
    for relative in by_path:
        main = paired_main(relative)
        if main is None:
            continue
        if main not in by_path:
            fail(f"Battle segment has no matching main theme: {relative} -> {main}")
        segments_by_main.setdefault(main, []).append(relative)

    methods: dict[str, dict[str, Any]] = {
        relative: {"method": "measuredTwoPassLoudnorm"} for relative in by_path
    }
    for main, segments in segments_by_main.items():
        members = [main, *segments]
        main_loudness = float(by_path[main]["input_i"])
        maximum_peak = max(float(by_path[member]["input_tp"]) for member in members)
        requested_gain = target_lufs - main_loudness
        safe_gain = min(requested_gain, true_peak - maximum_peak)
        group = {
            "method": "sharedBattleSegmentGain",
            "main": main,
            "members": sorted(members, key=str.casefold),
            "requestedGainDb": round(requested_gain, 6),
            "appliedGainDb": round(safe_gain, 6),
            "peakLimited": safe_gain < requested_gain - 1e-6,
        }
        for member in members:
            methods[member] = group
    return methods


def measured_filter(track: dict[str, Any], target_lufs: float, true_peak: float, target_lra: float) -> str:
    return (
        f"loudnorm=I={target_lufs:.2f}:TP={true_peak:.2f}:LRA={target_lra:.2f}:"
        f"measured_I={float(track['input_i']):.6f}:"
        f"measured_TP={float(track['input_tp']):.6f}:"
        f"measured_LRA={float(track['input_lra']):.6f}:"
        f"measured_thresh={float(track['input_thresh']):.6f}:"
        f"offset={float(track['target_offset']):.6f}:linear=true:print_format=json"
    )


def copy_non_audio(source_root: Path, output_root: Path) -> None:
    for source in source_root.rglob("*"):
        relative = source.relative_to(source_root)
        destination = output_root / relative
        if source.is_dir():
            destination.mkdir(parents=True, exist_ok=True)
            continue
        if source.suffix.lower() in AUDIO_EXTENSIONS:
            continue
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)


def set_battle_event_volumes(
    source_root: Path,
    output_root: Path,
    expected_references: int | None,
) -> dict[str, Any]:
    source_path = source_root / BATTLE_SOUNDS_PATH
    output_path = output_root / BATTLE_SOUNDS_PATH
    if not source_path.is_file() or not output_path.is_file():
        fail(f"Cobblemon battle sound registry is missing: {BATTLE_SOUNDS_PATH.as_posix()}")
    source_data = json.loads(source_path.read_text(encoding="utf-8"))
    output_data = json.loads(output_path.read_text(encoding="utf-8"))
    if source_data != output_data:
        fail("Copied Cobblemon sound registry changed before volume standardization")

    before: dict[str, int] = {}
    references = 0
    changed = 0
    for event_value in output_data.values():
        if not isinstance(event_value, dict):
            continue
        sounds = event_value.get("sounds", [])
        if not isinstance(sounds, list):
            continue
        for sound in sounds:
            if not isinstance(sound, dict):
                continue
            name = str(sound.get("name", ""))
            if not name.startswith("cobblemon:battle/"):
                continue
            effective_volume = float(sound.get("volume", 1.0))
            before[str(effective_volume)] = before.get(str(effective_volume), 0) + 1
            references += 1
            if effective_volume != 1.0 or "volume" not in sound:
                changed += 1
            sound["volume"] = 1.0

    if references == 0:
        fail("Cobblemon sound registry contains no battle music references")
    if expected_references is not None and references != expected_references:
        fail(
            f"Expected {expected_references} battle sound references, but discovered {references}"
        )
    write_json(output_path, output_data)
    return {
        "references": references,
        "changedOrMadeExplicit": changed,
        "effectiveVolumesBefore": before,
        "effectiveVolumeAfter": 1.0,
    }


def normalize_one(
    ffmpeg: Path,
    source_root: Path,
    output_root: Path,
    track: dict[str, Any],
    method: dict[str, Any],
    target_lufs: float,
    true_peak: float,
    target_lra: float,
) -> dict[str, Any]:
    relative = str(track["path"])
    source = source_root / Path(relative)
    destination = output_root / Path(relative)
    destination.parent.mkdir(parents=True, exist_ok=True)
    if method["method"] == "sharedBattleSegmentGain":
        audio_filter = f"volume={float(method['appliedGainDb']):.6f}dB:precision=double"
    else:
        audio_filter = measured_filter(track, target_lufs, true_peak, target_lra)

    suffix = source.suffix.lower()
    # This is an unavoidable lossy-to-lossy generation.  Use the encoders'
    # highest practical VBR quality settings so normalization does not also
    # become an audible quality downgrade.
    codec = ["-c:a", "libmp3lame", "-q:a", "0"] if suffix == ".mp3" else ["-c:a", "libvorbis", "-q:a", "8"]
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=destination.stem + ".normalizing.",
        suffix=destination.suffix,
        dir=destination.parent,
    )
    os.close(descriptor)
    temporary = Path(temporary_name)
    try:
        process = run_ffmpeg(
            ffmpeg,
            [
                "-hide_banner",
                "-nostdin",
                "-y",
                "-i",
                str(source),
                "-map",
                "0:a:0",
                "-map_metadata",
                "0",
                "-af",
                audio_filter,
                "-ar",
                str(int(track["sampleRate"])),
                "-threads",
                "1",
                *codec,
                str(temporary),
            ],
        )
        if process.returncode != 0 or not temporary.is_file() or temporary.stat().st_size == 0:
            tail = "\n".join(process.stderr.splitlines()[-20:])
            fail(f"FFmpeg normalization failed for {relative}:\n{tail}")
        filter_analysis: dict[str, Any] | None = None
        if method["method"] == "measuredTwoPassLoudnorm":
            matches = LOUDNORM_JSON.findall(process.stderr)
            if not matches:
                fail(f"FFmpeg omitted second-pass loudnorm telemetry for {relative}")
            raw_filter_analysis = json.loads(matches[-1])
            filter_analysis = {
                "normalizationType": raw_filter_analysis.get("normalization_type"),
                "inputI": number(raw_filter_analysis.get("input_i")),
                "inputTp": number(raw_filter_analysis.get("input_tp")),
                "inputLra": number(raw_filter_analysis.get("input_lra")),
                "outputI": number(raw_filter_analysis.get("output_i")),
                "outputTp": number(raw_filter_analysis.get("output_tp")),
                "outputLra": number(raw_filter_analysis.get("output_lra")),
                "targetOffset": number(raw_filter_analysis.get("target_offset")),
            }
        os.replace(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)
    return {
        "path": relative,
        "method": method["method"],
        "appliedGainDb": method.get("appliedGainDb"),
        "loudnorm": filter_analysis,
        "size": destination.stat().st_size,
        "sha256": sha256(destination),
    }


def normalize_all(
    ffmpeg: Path,
    source_root: Path,
    output_root: Path,
    tracks: list[dict[str, Any]],
    methods: dict[str, dict[str, Any]],
    jobs: int,
    target_lufs: float,
    true_peak: float,
    target_lra: float,
) -> list[dict[str, Any]]:
    if output_root.exists():
        fail(f"Output directory already exists; refusing to merge or overwrite: {output_root}")
    output_root.mkdir(parents=True)
    copy_non_audio(source_root, output_root)
    print(f"Normalizing {len(tracks)} tracks with {jobs} FFmpeg workers...", flush=True)
    results: list[dict[str, Any]] = []
    errors: list[str] = []

    def work(track: dict[str, Any]) -> dict[str, Any]:
        return normalize_one(
            ffmpeg,
            source_root,
            output_root,
            track,
            methods[str(track["path"])],
            target_lufs,
            true_peak,
            target_lra,
        )

    with concurrent.futures.ThreadPoolExecutor(max_workers=jobs) as executor:
        futures = {executor.submit(work, track): track for track in tracks}
        for index, future in enumerate(concurrent.futures.as_completed(futures), 1):
            track = futures[future]
            try:
                results.append(future.result())
            except Exception as exc:
                errors.append(f"{track['path']}: {exc}")
            if index % 25 == 0 or index == len(tracks):
                print(f"  normalized {index}/{len(tracks)}", flush=True)
    if errors:
        fail("Normalization failed:\n" + "\n".join(errors))
    results.sort(key=lambda item: str(item["path"]).casefold())
    return results


def verify_non_audio(source_root: Path, output_root: Path) -> None:
    source_files = {
        path.relative_to(source_root).as_posix(): path
        for path in source_root.rglob("*")
        if path.is_file() and path.suffix.lower() not in AUDIO_EXTENSIONS
    }
    output_files = {
        path.relative_to(output_root).as_posix(): path
        for path in output_root.rglob("*")
        if path.is_file()
        and path.suffix.lower() not in AUDIO_EXTENSIONS
        and path.name != "LOUDNESS_MANIFEST.json"
    }
    if set(source_files) != set(output_files):
        fail("Non-audio paths changed while normalizing the pack")
    for relative, source in source_files.items():
        if relative == BATTLE_SOUNDS_PATH.as_posix():
            continue
        if sha256(source) != sha256(output_files[relative]):
            fail(f"Non-audio file changed while normalizing: {relative}")


def verify_battle_event_volumes(
    source_root: Path,
    output_root: Path,
    expected_summary: dict[str, Any],
) -> None:
    source_data = json.loads(
        (source_root / BATTLE_SOUNDS_PATH).read_text(encoding="utf-8")
    )
    output_data = json.loads(
        (output_root / BATTLE_SOUNDS_PATH).read_text(encoding="utf-8")
    )
    references = 0
    for event_name, event_value in source_data.items():
        if not isinstance(event_value, dict):
            continue
        sounds = event_value.get("sounds", [])
        if not isinstance(sounds, list):
            continue
        for sound in sounds:
            if not isinstance(sound, dict):
                continue
            if not str(sound.get("name", "")).startswith("cobblemon:battle/"):
                continue
            references += 1
            sound["volume"] = 1.0
    if source_data != output_data:
        fail("Cobblemon sound registry differs beyond approved battle volume standardization")
    if references != int(expected_summary["references"]):
        fail("Cobblemon battle volume reference count changed during verification")


def verify_outputs(
    inputs: list[dict[str, Any]],
    outputs: list[dict[str, Any]],
    methods: dict[str, dict[str, Any]],
    target_lufs: float,
    true_peak: float,
) -> dict[str, Any]:
    input_by_path = {str(track["path"]): track for track in inputs}
    output_by_path = {str(track["path"]): track for track in outputs}
    if set(input_by_path) != set(output_by_path):
        fail("Normalized audio path inventory differs from the source inventory")
    failures: list[str] = []
    standalone_errors: list[float] = []
    group_gain_errors: list[float] = []
    group_main_target_errors: list[float] = []
    loudness_range_deltas: list[float] = []
    for relative, before in input_by_path.items():
        after = output_by_path[relative]
        if int(after["sampleRate"]) != int(before["sampleRate"]):
            failures.append(f"sample rate changed: {relative}")
        if str(after["channelLayout"]) != str(before["channelLayout"]):
            failures.append(
                f"channel layout changed: {relative} "
                f"({before['channelLayout']} -> {after['channelLayout']})"
            )
        if abs(float(after["durationSeconds"]) - float(before["durationSeconds"])) > 0.20:
            failures.append(f"duration changed by more than 0.20 seconds: {relative}")
        if float(after["input_tp"]) > true_peak + 0.50:
            failures.append(f"true peak exceeded tolerance: {relative} ({after['input_tp']} dBTP)")
        loudness_range_deltas.append(float(after["input_lra"]) - float(before["input_lra"]))
        method = methods[relative]
        if method["method"] == "measuredTwoPassLoudnorm":
            error = float(after["input_i"]) - target_lufs
            standalone_errors.append(error)
            if abs(error) > 0.50:
                failures.append(f"integrated loudness missed target: {relative} ({after['input_i']} LUFS)")
        else:
            if before.get("input_i") is not None and after.get("input_i") is not None:
                observed_gain = float(after["input_i"]) - float(before["input_i"])
            else:
                observed_gain = float(after["input_tp"]) - float(before["input_tp"])
            error = observed_gain - float(method["appliedGainDb"])
            group_gain_errors.append(error)
            if abs(error) > 0.50:
                failures.append(f"shared segment gain mismatch: {relative} ({error:+.3f} dB)")
            if relative == str(method["main"]):
                target_error = float(after["input_i"]) - target_lufs
                group_main_target_errors.append(target_error)
                if abs(target_error) > 0.75:
                    failures.append(
                        f"paired main theme missed loudness target: {relative} "
                        f"({after['input_i']} LUFS)"
                    )
    if failures:
        fail("Normalized output verification failed:\n" + "\n".join(failures))
    return {
        "verifiedTracks": len(outputs),
        "maximumStandaloneTargetErrorLu": round(max(map(abs, standalone_errors), default=0.0), 4),
        "maximumSharedGainErrorDb": round(max(map(abs, group_gain_errors), default=0.0), 4),
        "maximumPairedMainTargetErrorLu": round(
            max(map(abs, group_main_target_errors), default=0.0), 4
        ),
        "pairedMainTargetToleranceLu": 0.75,
        "loudnessRangeChangeLu": {
            "minimum": round(min(loudness_range_deltas), 4),
            "median": round(percentile(loudness_range_deltas, 0.5) or 0.0, 4),
            "maximum": round(max(loudness_range_deltas), 4),
            "tracksChangedByMoreThan1Lu": sum(
                1 for delta in loudness_range_deltas if abs(delta) > 1.0
            ),
        },
        "truePeakToleranceDb": 0.50,
        "durationToleranceSeconds": 0.20,
    }


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".new")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def load_analysis(path: Path) -> dict[str, Any]:
    if not path.is_file():
        fail(f"Analysis report does not exist: {path}")
    data = json.loads(path.read_text(encoding="utf-8"))
    if data.get("schemaVersion") != 1 or not isinstance(data.get("tracks"), list):
        fail(f"Analysis report has an unsupported format: {path}")
    return data


def validate_ffmpeg(path: str) -> Path:
    ffmpeg = Path(path).expanduser().resolve()
    if not ffmpeg.is_file():
        fail(f"FFmpeg executable does not exist: {ffmpeg}")
    process = run_ffmpeg(ffmpeg, ["-version"])
    if process.returncode != 0 or "ffmpeg version" not in process.stdout:
        fail(f"FFmpeg executable failed its version check: {ffmpeg}")
    return ffmpeg


def arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    for name in ("analyze", "normalize"):
        command = subparsers.add_parser(name)
        command.add_argument("--source", required=True)
        command.add_argument("--ffmpeg", required=True)
        command.add_argument("--report", required=True)
        command.add_argument("--jobs", type=int, default=max(1, min(6, os.cpu_count() or 1)))
        command.add_argument("--target-lufs", type=float, default=-16.0)
        command.add_argument("--true-peak", type=float, default=-1.5)
        command.add_argument("--target-lra", type=float, default=11.0)
        command.add_argument("--expected-audio-count", type=int)
        command.add_argument("--expected-paired-segments", type=int)
        command.add_argument("--expected-battle-sound-references", type=int)
        if name == "normalize":
            command.add_argument("--output", required=True)
    return parser.parse_args()


def main() -> int:
    args = arguments()
    if args.jobs < 1 or args.jobs > 24:
        fail("--jobs must be between 1 and 24")
    if not -30.0 <= args.target_lufs <= -10.0:
        fail("--target-lufs must be between -30 and -10")
    if not -6.0 <= args.true_peak <= -0.1:
        fail("--true-peak must be between -6 and -0.1")
    if not 1.0 <= args.target_lra <= 20.0:
        fail("--target-lra must be between 1 and 20")

    source_root = canonical_root(args.source, must_exist=True)
    ensure_plain_tree(source_root)
    ffmpeg = validate_ffmpeg(args.ffmpeg)
    report_path = Path(args.report).expanduser().resolve()
    if report_path == source_root or source_root in report_path.parents:
        fail("Analysis report must be outside the source pack tree")
    files = discover_audio(source_root)
    if args.expected_audio_count is not None and len(files) != args.expected_audio_count:
        fail(
            f"Expected {args.expected_audio_count} audio files, but discovered {len(files)}"
        )

    if args.command == "analyze":
        tracks = analyze_all(
            ffmpeg,
            source_root,
            files,
            args.jobs,
            args.target_lufs,
            args.true_peak,
            args.target_lra,
        )
        pairing = pairing_summary(tracks)
        if (
            args.expected_paired_segments is not None
            and pairing["pairedSegments"] != args.expected_paired_segments
        ):
            fail(
                f"Expected {args.expected_paired_segments} paired battle segments, "
                f"but discovered {pairing['pairedSegments']}"
            )
        report = {
            "schemaVersion": 1,
            "sourceRoot": str(source_root),
            "ffmpegPath": str(ffmpeg),
            "ffmpegSha256": sha256(ffmpeg),
            "target": {
                "integratedLufs": args.target_lufs,
                "truePeakDbtp": args.true_peak,
                "loudnessRangeLu": args.target_lra,
            },
            "summary": summarize(tracks),
            "battlePairing": pairing,
            "tracks": tracks,
        }
        write_json(report_path, report)
        print(json.dumps(report["summary"], indent=2), flush=True)
        print(f"Wrote analysis report: {report_path}", flush=True)
        return 0

    analysis = load_analysis(report_path)
    current_ffmpeg_sha256 = sha256(ffmpeg)
    if str(analysis.get("ffmpegSha256", "")).casefold() != current_ffmpeg_sha256.casefold():
        fail("Analysis report was produced by a different FFmpeg binary")
    target = analysis.get("target", {})
    expected_target = (args.target_lufs, args.true_peak, args.target_lra)
    actual_target = (
        number(target.get("integratedLufs")),
        number(target.get("truePeakDbtp")),
        number(target.get("loudnessRangeLu")),
    )
    if actual_target != expected_target:
        fail(f"Analysis target {actual_target} does not match requested normalization target {expected_target}")
    tracks = list(analysis["tracks"])
    pairing = pairing_summary(tracks)
    if (
        args.expected_paired_segments is not None
        and pairing["pairedSegments"] != args.expected_paired_segments
    ):
        fail(
            f"Expected {args.expected_paired_segments} paired battle segments, "
            f"but discovered {pairing['pairedSegments']}"
        )
    current_paths = {path.relative_to(source_root).as_posix() for path in files}
    if current_paths != {str(track["path"]) for track in tracks}:
        fail("Source audio inventory changed after the analysis report was created")
    for track in tracks:
        source = source_root / Path(str(track["path"]))
        if source.stat().st_size != int(track["size"]) or sha256(source) != str(track["sha256"]):
            fail(f"Source audio changed after analysis: {track['path']}")

    output_root = canonical_root(args.output, must_exist=False)
    if output_root == source_root or source_root in output_root.parents:
        fail("Output must be outside the source pack tree")
    methods = build_methods(tracks, args.target_lufs, args.true_peak)
    normalized = normalize_all(
        ffmpeg,
        source_root,
        output_root,
        tracks,
        methods,
        args.jobs,
        args.target_lufs,
        args.true_peak,
        args.target_lra,
    )
    runtime_volume = set_battle_event_volumes(
        source_root,
        output_root,
        args.expected_battle_sound_references,
    )
    output_files = discover_audio(output_root)
    output_analysis = analyze_all(
        ffmpeg,
        output_root,
        output_files,
        args.jobs,
        args.target_lufs,
        args.true_peak,
        args.target_lra,
    )
    verify_non_audio(source_root, output_root)
    verify_battle_event_volumes(source_root, output_root, runtime_volume)
    verification = verify_outputs(tracks, output_analysis, methods, args.target_lufs, args.true_peak)
    normalization_by_path = {str(item["path"]): item for item in normalized}
    output_by_path = {str(item["path"]): item for item in output_analysis}
    manifest_tracks = []
    for before in tracks:
        relative = str(before["path"])
        manifest_tracks.append(
            {
                "path": relative,
                "method": methods[relative]["method"],
                "pairedMain": methods[relative].get("main"),
                "appliedGainDb": methods[relative].get("appliedGainDb"),
                "input": before,
                "output": output_by_path[relative],
                "outputFile": normalization_by_path[relative],
            }
        )
    manifest = {
        "schemaVersion": 1,
        "standard": "EBU R128 / ITU-R BS.1770 (FFmpeg loudnorm)",
        "target": {
            "integratedLufs": args.target_lufs,
            "truePeakDbtp": args.true_peak,
            "loudnessRangeLu": args.target_lra,
        },
        "ffmpegSha256": sha256(ffmpeg),
        "encoding": {
            "mp3": "libmp3lame VBR quality 0",
            "ogg": "libvorbis VBR quality 8",
            "metadata": "copied from source where supported by the output container",
        },
        "processing": {
            "measuredTwoPass": sum(
                1 for item in normalized if item["method"] == "measuredTwoPassLoudnorm"
            ),
            "sharedBattleSegmentGain": sum(
                1 for item in normalized if item["method"] == "sharedBattleSegmentGain"
            ),
            "twoPassLinear": sum(
                1
                for item in normalized
                if (item.get("loudnorm") or {}).get("normalizationType") == "linear"
            ),
            "twoPassDynamic": sum(
                1
                for item in normalized
                if (item.get("loudnorm") or {}).get("normalizationType") == "dynamic"
            ),
        },
        "runtimeVolume": runtime_volume,
        "sourceSummary": summarize(tracks),
        "normalizedSummary": summarize(output_analysis),
        "verification": verification,
        "battlePairing": pairing,
        "tracks": manifest_tracks,
    }
    write_json(output_root / "LOUDNESS_MANIFEST.json", manifest)
    print(json.dumps(manifest["normalizedSummary"], indent=2), flush=True)
    print(json.dumps(verification, indent=2), flush=True)
    print(f"Wrote normalized pack tree: {output_root}", flush=True)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr, flush=True)
        raise SystemExit(1)
