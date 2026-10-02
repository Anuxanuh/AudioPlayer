import contextlib
import hashlib
import io
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import model_manager

class ModelManagerTests(unittest.TestCase):
    def test_download_pins_revision_verifies_and_installs_without_touching_complete_model(self):
        payload = {"model.bin": b"weights", "config.json": b"{}", "tokenizer.json": b"{}"}
        files = [SimpleNamespace(rfilename=name, size=len(data), lfs=SimpleNamespace(sha256=hashlib.sha256(data).hexdigest()) if name == "model.bin" else None) for name, data in payload.items()]
        calls = []
        def snapshot(repo, **kwargs):
            calls.append((repo, kwargs))
            for name, data in payload.items(): (Path(kwargs["local_dir"]) / name).write_bytes(data)
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            with patch.object(model_manager, "metadata", return_value=("a" * 40, files)), patch("huggingface_hub.snapshot_download", side_effect=snapshot), contextlib.redirect_stdout(io.StringIO()):
                model_manager.download(root, "tiny")
                model_manager.download(root, "tiny")
            self.assertEqual(len(calls), 1)
            self.assertEqual(calls[0][1]["revision"], "a" * 40)
            self.assertFalse(calls[0][1]["token"])
            final = root / "faster-whisper-tiny"
            self.assertTrue(model_manager.complete(final))
            self.assertEqual(json.loads((final / "download-manifest.json").read_text(encoding="utf-8"))["files"][0]["sha256"], hashlib.sha256(b"weights").hexdigest())

    def test_failed_verification_never_exposes_incomplete_model(self):
        with tempfile.TemporaryDirectory() as folder:
            stage = Path(folder) / ".downloads" / "tiny"; stage.mkdir(parents=True)
            (stage / "model.bin").write_bytes(b"bad")
            file = SimpleNamespace(rfilename="model.bin", size=3, lfs=SimpleNamespace(sha256="0" * 64))
            with self.assertRaisesRegex(ValueError, "SHA-256"):
                model_manager.verify_files(stage, [file])
            self.assertFalse((Path(folder) / "faster-whisper-tiny").exists())

    def test_incomplete_existing_folder_is_preserved_during_install(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); destination = root / "faster-whisper-tiny"; destination.mkdir()
            (destination / "keep.txt").write_text("old file")
            stage = root / ".downloads" / "tiny"; stage.mkdir(parents=True)
            (stage / "model.bin").write_text("new")
            model_manager.install_stage(stage, destination, root)
            self.assertEqual((destination / "model.bin").read_text(), "new")
            self.assertEqual(next((root / ".backups").rglob("keep.txt")).read_text(), "old file")

if __name__ == "__main__": unittest.main()
