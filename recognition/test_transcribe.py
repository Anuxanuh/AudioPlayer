import contextlib
import io
import json
import os
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import tempfile
import sys
import threading

import transcribe


class LyricsTests(unittest.TestCase):
    def test_simplified_conversion_matches_stream_and_lrc_and_can_be_disabled(self):
        text = "聲音讓我們聽見遠方，頭髮與發展，Hello 123 🌊"
        expected = "声音让我们听见远方，头发与发展，Hello 123 🌊"
        for enabled in (True, False):
            with self.subTest(enabled=enabled):
                lines, events, progress = io.StringIO(), [], []
                count = transcribe.write_lrc([SimpleNamespace(start=1.25, end=3.5, text=text)], lines, 5,
                    progress=lambda pct, msg: progress.append(msg), on_segment=lambda start, end, msg: events.append((start, end, msg)),
                    convert_text=transcribe.create_text_converter(enabled))
                result = expected if enabled else text
                self.assertEqual(count, 1)
                self.assertEqual(lines.getvalue(), f"[00:01.25]{result}\n[00:03.50]\n")
                self.assertEqual(events, [(1.25, 3.5, result)])
                self.assertEqual(progress, [result])

    def test_worker_simplified_flag_applies_to_single_and_parallel_batch(self):
        class FakeModel:
            def __init__(self, *args, **kwargs): pass
            def transcribe(self, *args, **kwargs):
                return iter([SimpleNamespace(start=0, end=1, text="繁體歌詞與音樂")]), SimpleNamespace(duration=2, language="zh")
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name in ("model.bin", "config.json", "tokenizer.json", "語音.wav"):
                (root / name).write_text("test", encoding="utf-8")
            for batch in (False, True):
                for simplified in (False, True):
                    with self.subTest(batch=batch, simplified=simplified):
                        outputs = [root / f"{i}.lrc" for i in range(2 if batch else 1)]
                        args = ["transcribe.py", "--model", folder, "--stream"]
                        if simplified: args.append("--simplified")
                        if batch:
                            manifest = root / "batch.json"
                            manifest.write_text(json.dumps([{"index":i, "audio":str(root / "語音.wav"), "temporary":str(path)} for i, path in enumerate(outputs)]), encoding="utf-8")
                            args += ["--batch", str(manifest), "--workers", "2"]
                        else: args += ["--audio", str(root / "語音.wav"), "--output", str(outputs[0])]
                        stdout = io.StringIO()
                        with patch.object(sys, "argv", args), patch.dict(sys.modules, {"faster_whisper": SimpleNamespace(WhisperModel=FakeModel)}), contextlib.redirect_stdout(stdout):
                            self.assertEqual(transcribe.main(), 0)
                        expected = "繁体歌词与音乐" if simplified else "繁體歌詞與音樂"
                        self.assertTrue(all(expected in path.read_text(encoding="utf-8") for path in outputs))
                        segments = [e for line in stdout.getvalue().splitlines() if (e := json.loads(line)).get("type", e.get("state")) == "segment"]
                        self.assertEqual(len(segments), len(outputs))
                        self.assertTrue(all(e["text"] == expected for e in segments))

    def test_parallel_files_share_one_model_and_use_two_threads(self):
        loaded, threads = [], set()
        barrier = threading.Barrier(2, timeout=5)
        class FakeModel:
            def __init__(self, path, **kwargs): loaded.append(kwargs)
            def transcribe(self, path, **kwargs):
                threads.add(threading.get_ident())
                barrier.wait()
                return iter([SimpleNamespace(start=0, end=1, text=Path(path).stem)]), SimpleNamespace(duration=2, language="zh")
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name in ("model.bin", "config.json", "tokenizer.json", "甲.wav", "乙.wav"):
                (root / name).write_text("fixture", encoding="utf-8")
            jobs = [{"index": i, "audio": str(root / name), "temporary": str(root / f"{i}.tmp")} for i, name in enumerate(("甲.wav", "乙.wav"))]
            manifest = root / "batch.json"
            manifest.write_text(json.dumps(jobs), encoding="utf-8")
            output = io.StringIO()
            with patch.object(sys, "argv", ["transcribe.py", "--model", folder, "--batch", str(manifest), "--workers", "2"]), patch.dict(sys.modules, {"faster_whisper": SimpleNamespace(WhisperModel=FakeModel)}), contextlib.redirect_stdout(output):
                self.assertEqual(transcribe.main(), 0)
            self.assertEqual(len(loaded), 1)
            self.assertEqual(loaded[0]["num_workers"], 2)
            self.assertEqual(len(threads), 2)
            events = [json.loads(line) for line in output.getvalue().splitlines()]
            self.assertEqual(sum(e.get("state") == "completed" for e in events), 2)
            self.assertIn("甲", (root / "0.tmp").read_text(encoding="utf-8"))
            self.assertIn("乙", (root / "1.tmp").read_text(encoding="utf-8"))

    def test_pause_retains_iterator_model_and_progress_until_resumed(self):
        loaded, yielded, events, errors = [], [], [], []
        paused = threading.Event()
        class FakeModel:
            def __init__(self, *args, **kwargs): loaded.append(1)
            def transcribe(self, *args, **kwargs):
                def segments():
                    for i in range(3):
                        yielded.append(i)
                        yield SimpleNamespace(start=i, end=i+1, text=f"片段{i}")
                return segments(), SimpleNamespace(duration=3, language="zh")
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name in ("model.bin", "config.json", "tokenizer.json", "语音.wav"):
                (root / name).write_text("fixture", encoding="utf-8")
            signal = root / "pause"
            output = root / "output.tmp"
            manifest = root / "batch.json"
            manifest.write_text(json.dumps([{"index":0, "audio":str(root / "语音.wav"), "temporary":str(output)}]), encoding="utf-8")
            def report(percent, message, **extra):
                events.append({"percent":percent, **extra})
                if extra.get("state") == "segment" and extra.get("start") == 0: signal.touch()
                if extra.get("state") == "paused": paused.set()
            def run():
                try: transcribe.main()
                except BaseException as error: errors.append(error)
            with patch.object(sys, "argv", ["transcribe.py", "--model", folder, "--batch", str(manifest), "--pause-file", str(signal)]), patch.dict(sys.modules, {"faster_whisper": SimpleNamespace(WhisperModel=FakeModel)}), patch.object(transcribe, "report", report):
                thread = threading.Thread(target=run)
                thread.start()
                try:
                    self.assertTrue(paused.wait(5))
                    self.assertTrue(thread.is_alive())
                    self.assertEqual(yielded, [0])
                    self.assertIn("片段0", output.read_text(encoding="utf-8"))
                    self.assertAlmostEqual(next(e["percent"] for e in events if e.get("state") == "paused"), 100/3)
                finally:
                    signal.unlink(missing_ok=True)
                    thread.join(5)
                self.assertFalse(thread.is_alive())
            self.assertFalse(errors)
            self.assertEqual(loaded, [1])
            self.assertEqual(yielded, [0,1,2])
            self.assertEqual(output.read_text(encoding="utf-8").count("片段"), 3)
            self.assertEqual(events[-1]["state"], "completed")

    def test_batch_loads_model_once_and_continues_after_failure(self):
        loaded, transcribed = [], []
        class FakeModel:
            def __init__(self, path, **kwargs): loaded.append(path)
            def transcribe(self, path, **kwargs):
                transcribed.append(path)
                if Path(path).name == "坏音频.wav": raise ValueError("bad audio")
                return iter([SimpleNamespace(start=0, end=1, text="批量歌词")]), SimpleNamespace(duration=2, language="zh")
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name in ("model.bin", "config.json", "tokenizer.json", "第一首.wav", "坏音频.wav", "第三首.wav"):
                (root / name).write_text("test", encoding="utf-8")
            jobs = [{"index": i, "audio": str(root / name), "temporary": str(root / f"{i}.tmp")} for i, name in enumerate(("第一首.wav", "坏音频.wav", "第三首.wav"))]
            manifest = root / "batch.json"
            manifest.write_text(json.dumps(jobs), encoding="utf-8")
            output = io.StringIO()
            with patch.object(sys, "argv", ["transcribe.py", "--model", folder, "--batch", str(manifest)]), patch.dict(sys.modules, {"faster_whisper": SimpleNamespace(WhisperModel=FakeModel)}), contextlib.redirect_stdout(output):
                self.assertEqual(transcribe.main(), 0)
            self.assertEqual(len(loaded), 1)
            self.assertEqual(len(transcribed), 3)
            events = [json.loads(line) for line in output.getvalue().splitlines()]
            self.assertEqual([(e["index"], e["state"]) for e in events if e.get("state") in ("completed", "failed")], [(0, "completed"), (1, "failed"), (2, "completed")])
            self.assertIn("批量歌词", (root / "2.tmp").read_text(encoding="utf-8"))

    def test_timestamp_carry_and_long_recordings(self):
        self.assertEqual(transcribe.timestamp(59.999), "[01:00.00]")
        self.assertEqual(transcribe.timestamp(3600.12), "[60:00.12]")
        self.assertEqual(transcribe.timestamp(-1), "[00:00.00]")

    def test_empty_segments_and_silence(self):
        segments = [SimpleNamespace(start=1.5, end=3.0, text="  你好\n世界 [合唱] "),
                    SimpleNamespace(start=4, end=5, text=" ")]
        output = io.StringIO()
        with contextlib.redirect_stdout(io.StringIO()):
            count = transcribe.write_lrc(segments, output, 5)
        self.assertEqual(count, 1)
        self.assertEqual(output.getvalue(), "[00:01.50]你好 世界 （合唱）\n[00:03.00]\n")

    def test_requires_complete_local_model_before_import(self):
        with tempfile.TemporaryDirectory() as folder:
            with patch.object(sys, "argv", ["transcribe.py", "--model", folder, "--check"]):
                with self.assertRaisesRegex(ValueError, "model.bin"):
                    transcribe.main()

    def test_worker_uses_offline_local_model_and_writes_utf8(self):
        calls = []
        class FakeModel:
            def __init__(self, path, **kwargs):
                calls.append((path, kwargs))
            def transcribe(self, path, **kwargs):
                calls.append((path, kwargs))
                return iter([SimpleNamespace(start=0.12, end=1.23, text="本地识别")]), SimpleNamespace(duration=2, language="zh")
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name in ("model.bin", "config.json", "tokenizer.json", "preprocessor_config.json", "语音.wav"):
                (root / name).write_text("test", encoding="utf-8")
            output = root / "语音.lrc"
            arguments = ["transcribe.py", "--model", folder, "--audio", str(root / "语音.wav"), "--output", str(output), "--language", "zh"]
            with patch.object(sys, "argv", arguments), patch.dict(sys.modules, {"faster_whisper": SimpleNamespace(WhisperModel=FakeModel)}), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(transcribe.main(), 0)
            self.assertTrue(calls[0][1]["local_files_only"])
            self.assertEqual(calls[0][1]["compute_type"], "int8")
            self.assertEqual(calls[1][1]["language"], "zh")
            self.assertFalse(calls[1][1]["vad_filter"])
            self.assertIn("[00:00.12]本地识别", output.read_text(encoding="utf-8"))
            self.assertEqual(os.environ["HF_HUB_OFFLINE"], "1")


if __name__ == "__main__":
    unittest.main()
