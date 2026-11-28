"""Renders the guided tour's lines to MP3 with Kokoro, an offline neural voice.

Explorer2 plays Tour/voice/<language>/<line id>.mp3 when voice.json says it was made from the
line's current text, and reads any other line with the Windows voice. Run this again after
editing lines in Tour/tour.<language>.json; only lines whose text changed are rendered.

Setup (once, outside the repo):
    py -3.12 -m venv <dir>/venv
    <dir>/venv/Scripts/python.exe -m pip install kokoro-onnx soundfile lameenc
    Download kokoro-v1.0.onnx and voices-v1.0.bin from
    https://github.com/thewh1teagle/kokoro-onnx/releases (model-files-v1.0)

Run from the repo root:
    <dir>/venv/Scripts/python.exe tools/render-tour-voice.py --model <dir>/kokoro-v1.0.onnx --voices <dir>/voices-v1.0.bin
"""

import argparse
import hashlib
import json
import pathlib

import lameenc
import numpy as np
from kokoro_onnx import Kokoro


def text_hash(text: str) -> str:
    """Same as TourVoice.TextHash: SHA-256 of the UTF-8 text, first 16 hex digits."""
    return hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]


def read_script(path: pathlib.Path) -> dict:
    # tour.*.json allows // comment lines
    lines = [l for l in path.read_text(encoding="utf-8").splitlines() if not l.lstrip().startswith("//")]
    return json.loads("\n".join(lines))


def to_mp3(samples: np.ndarray, rate: int) -> bytes:
    pcm = (np.clip(samples, -1.0, 1.0) * 32767).astype(np.int16).tobytes()
    encoder = lameenc.Encoder()
    encoder.set_bit_rate(64)
    encoder.set_in_sample_rate(rate)
    encoder.set_channels(1)
    encoder.set_quality(2)
    return encoder.encode(pcm) + encoder.flush()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--model", required=True)
    parser.add_argument("--voices", required=True)
    parser.add_argument("--script", default="Tour/tour.en.json")
    parser.add_argument("--voice", default="af_heart")
    parser.add_argument("--speed", type=float, default=1.0)
    parser.add_argument("--all", action="store_true", help="render every line, not only changed ones")
    args = parser.parse_args()

    script = read_script(pathlib.Path(args.script))
    language = script.get("language", "en-US")
    out = pathlib.Path("Tour/voice") / language
    out.mkdir(parents=True, exist_ok=True)
    manifest_path = out / "voice.json"
    old = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else {}
    same_voice = old.get("voice") == args.voice and old.get("speed") == args.speed
    old_lines = old.get("lines", {}) if same_voice else {}

    kokoro = Kokoro(args.model, args.voices)
    lines: dict[str, str] = script["lines"]
    hashes: dict[str, str] = {}
    for key, text in lines.items():
        digest = text_hash(text)
        mp3 = out / f"{key}.mp3"
        if not args.all and old_lines.get(key) == digest and mp3.exists():
            hashes[key] = digest
            continue

        samples, rate = kokoro.create(text, voice=args.voice, speed=args.speed, lang=language.lower())
        mp3.write_bytes(to_mp3(samples, rate))
        hashes[key] = digest
        print(f"rendered {key} ({len(samples) / rate:.1f} s)")

    for stale in out.glob("*.mp3"):
        if stale.stem not in lines:
            stale.unlink()
            print(f"removed {stale.name}")

    manifest = {"voice": f"{args.voice}", "speed": args.speed, "engine": "Kokoro v1.0", "lines": hashes}
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"{len(hashes)} lines in {out}")


if __name__ == "__main__":
    main()
