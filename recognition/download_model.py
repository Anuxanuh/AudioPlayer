"""Download the explicit, versioned catalog of CTranslate2 models. No inference network use."""
import argparse
import fnmatch
import hashlib
import json
from pathlib import Path
import shutil
import time
from huggingface_hub import HfApi, snapshot_download

PATTERNS = ("model.bin", "config.json", "tokenizer.json", "preprocessor_config.json", "vocabulary.*", "README.md", "LICENSE*")
def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--size", default="small")
    parser.add_argument("--all", action="store_true")
    parser.add_argument("--output", required=True, help="Model directory for --size, parent models directory for --all")
    parser.add_argument("--plan", action="store_true")
    args = parser.parse_args()
    catalog = json.loads(Path(__file__).with_name("model_catalog.json").read_text(encoding="utf-8-sig"))
    selected = catalog if args.all else [m for m in catalog if m["id"] == args.size]
    if not selected: parser.error("Unknown model; see model_catalog.json")
    root = Path(args.output).resolve()
    root.mkdir(parents=True, exist_ok=True)
    api = HfApi()
    plans = []
    for model in selected:
        info = api.model_info(model["repo"], files_metadata=True)
        files = [f for f in info.siblings if any(fnmatch.fnmatch(f.rfilename, p) for p in PATTERNS)]
        size = sum(f.size or 0 for f in files)
        destination = root / ("faster-whisper-" + model["id"]) if args.all else root
        plans.append((model, info.sha, files, destination))
        print(json.dumps({"model": model["id"], "bytes": size, "revision": info.sha}, ensure_ascii=False), flush=True)
    total = sum(f.size or 0 for _, _, files, _ in plans for f in files)
    print(f"Total catalog size: {total / 1024**3:.2f} GiB", flush=True)
    if args.plan: return
    if shutil.disk_usage(root).free < total:
        raise RuntimeError("Not enough free disk space for a complete model catalog")
    failures = []
    for index, (model, revision, files, destination) in enumerate(plans, 1):
        for attempt in range(3):
            try:
                print(f"[{index}/{len(plans)}] Downloading {model['id']}", flush=True)
                snapshot_download(repo_id=model["repo"], revision=revision, local_dir=str(destination), allow_patterns=PATTERNS, max_workers=4)
                for name in ("model.bin", "config.json", "tokenizer.json"):
                    if not (destination / name).is_file(): raise RuntimeError(f"Missing {name}")
                manifest_files = []
                for f in files:
                    path = destination / f.rfilename
                    if not path.is_file() or (f.size is not None and path.stat().st_size != f.size):
                        raise RuntimeError(f"Invalid downloaded file size: {path}")
                    sha = None
                    if f.lfs is not None:
                        sha = hashlib.file_digest(path.open("rb"), "sha256").hexdigest()
                        if sha != f.lfs.sha256: raise RuntimeError(f"Checksum mismatch: {path}")
                    manifest_files.append({"name": f.rfilename, "bytes": path.stat().st_size, "sha256": sha})
                manifest = {**model, "revision": revision, "files": manifest_files}
                (destination / "download-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
                print(f"VERIFIED {model['id']}", flush=True)
                break
            except Exception as exc:
                print(f"Attempt {attempt + 1} failed for {model['id']}: {exc}", flush=True)
                if attempt == 2: failures.append(model["id"])
                else: time.sleep(3)
    if failures: raise RuntimeError("Incomplete models: " + ", ".join(failures))
    print("All selected models downloaded and verified.", flush=True)

if __name__ == "__main__":
    main()

