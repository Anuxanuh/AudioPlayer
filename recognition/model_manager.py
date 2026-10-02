"""Explicit online model browsing/download. Never used by the offline transcription worker."""
import argparse
import concurrent.futures
import fnmatch
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import threading
import uuid

os.environ["HF_HUB_OFFLINE"] = "0"
os.environ["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
os.environ["HF_HUB_ETAG_TIMEOUT"] = "15"
os.environ["HF_HUB_DOWNLOAD_TIMEOUT"] = "30"

PATTERNS = ("model.bin", "config.json", "tokenizer.json", "preprocessor_config.json", "vocabulary.*", "README.md", "LICENSE*")
REQUIRED = ("model.bin", "config.json", "tokenizer.json")
ENDPOINT = "https://huggingface.co"

def emit(kind, **data):
    print(json.dumps({"type": kind, **data}, ensure_ascii=False), flush=True)

def catalog():
    return json.loads(Path(__file__).with_name("model_catalog.json").read_text(encoding="utf-8-sig"))

def complete(path):
    return all((path / name).is_file() and (path / name).stat().st_size > 0 for name in REQUIRED)

def metadata(model):
    from huggingface_hub import HfApi
    info = HfApi(endpoint=ENDPOINT, token=False).model_info(model["repo"], files_metadata=True, timeout=15)
    files = [f for f in info.siblings if "/" not in f.rfilename and "\\" not in f.rfilename and any(fnmatch.fnmatch(f.rfilename, p) for p in PATTERNS)]
    if not all(name in {f.rfilename for f in files} for name in REQUIRED):
        raise ValueError("在线仓库缺少必要的 CTranslate2 模型文件。")
    return info.sha, files

def browse(root):
    def one(model):
        try:
            revision, files = metadata(model)
            return {**model, "revision": revision, "bytes": sum(f.size or 0 for f in files), "local": complete(root / ("faster-whisper-" + model["id"])), "error": ""}
        except Exception as exc:
            return {**model, "error": str(exc)}
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        emit("catalog", models=list(pool.map(one, catalog())))

def verify_files(stage, files):
    manifest = []
    for file in files:
        path = stage / file.rfilename
        if not path.is_file() or file.size is None or path.stat().st_size != file.size:
            raise ValueError(f"模型文件大小校验失败：{file.rfilename}")
        sha = None
        if file.lfs:
            with path.open("rb") as stream: sha = hashlib.file_digest(stream, "sha256").hexdigest()
            if sha != file.lfs.sha256: raise ValueError(f"SHA-256 校验失败：{file.rfilename}")
        manifest.append({"name": file.rfilename, "bytes": file.size, "sha256": sha})
    return manifest

def install_stage(stage, destination, root):
    # A partially existing model is preserved rather than deleted. Both moves stay in this model root.
    backup = None
    if destination.exists():
        backup = root / ".backups" / (destination.name + "-" + uuid.uuid4().hex)
        backup.parent.mkdir(parents=True, exist_ok=True)
        destination.rename(backup)
    try:
        stage.rename(destination)
    except Exception:
        if backup and not destination.exists(): backup.rename(destination)
        raise

def download(root, model_id):
    from huggingface_hub import snapshot_download
    model = next((m for m in catalog() if m["id"] == model_id), None)
    if model is None: raise ValueError("请选择受支持的多语言模型。")
    root.mkdir(parents=True, exist_ok=True)
    destination = root / ("faster-whisper-" + model_id)
    if complete(destination):
        emit("completed", path=str(destination), message="本地模型已完整，无须重复下载。")
        return
    emit("progress", percent=0, message="正在读取在线模型版本…")
    revision, files = metadata(model)
    if not revision or not all(c in "0123456789abcdef" for c in revision): raise ValueError("无效的在线版本标识。")
    stage = root / ".downloads" / (model_id + "-" + revision)
    stage.mkdir(parents=True, exist_ok=True)
    total = sum(f.size or 0 for f in files)
    existing = sum(p.stat().st_size for p in stage.rglob("*") if p.is_file())
    if shutil.disk_usage(root).free < max(0, total - existing) + 64 * 1024**2: raise RuntimeError("模型目录所在磁盘空间不足。")
    stop = threading.Event()
    def monitor():
        while not stop.wait(0.5):
            try:
                downloaded = sum((stage / f.rfilename).stat().st_size for f in files if (stage / f.rfilename).is_file())
                downloaded += sum(p.stat().st_size for p in stage.rglob("*.incomplete"))
                emit("progress", percent=min(99, downloaded / max(1, total) * 100), message=f"下载 {model_id}：{downloaded / 1024**2:.1f} / {total / 1024**2:.1f} MiB")
            except OSError: pass  # File rename while measuring progress.
    monitor_thread = threading.Thread(target=monitor, daemon=True)
    monitor_thread.start()
    try:
        snapshot_download(model["repo"], revision=revision, local_dir=str(stage), endpoint=ENDPOINT,
                          allow_patterns=[f.rfilename for f in files], max_workers=2, token=False)
    finally:
        stop.set()
        monitor_thread.join(timeout=1)
    emit("progress", percent=99, message="下载完成，正在校验文件大小与 SHA-256…")
    checked = verify_files(stage, files)
    (stage / "download-manifest.json").write_text(json.dumps({**model, "revision": revision, "files": checked}, ensure_ascii=False, indent=2), encoding="utf-8")
    install_stage(stage, destination, root)
    emit("completed", path=str(destination), message=f"{model_id} 已下载并校验，可用于离线识别。")

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", required=True)
    parser.add_argument("--list", action="store_true")
    parser.add_argument("--download")
    args = parser.parse_args()
    root = Path(args.root).expanduser().resolve()
    if args.list: browse(root)
    elif args.download: download(root, args.download)
    else: parser.error("需要 --list 或 --download")

if __name__ == "__main__":
    try: main()
    except Exception as exc:
        print(f"{type(exc).__name__}: {exc}", file=sys.stderr, flush=True)
        sys.exit(1)
