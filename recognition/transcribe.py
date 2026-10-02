"""Offline faster-whisper worker. Stdout is JSONL; diagnostics go to stderr."""
from __future__ import annotations

import argparse
import concurrent.futures
import json
import math
import os
from pathlib import Path
import sys
import threading
import time
sys.path.insert(0, str(Path(__file__).resolve().parent))
from gpu_runtime import configure_dll_directories

# These are set before importing any model library. Inference never downloads models.
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
_report_lock = threading.Lock()

def report(percent: float, message: str, **extra) -> None:
    with _report_lock:
        print(json.dumps({"percent": percent, "message": message, **extra}, ensure_ascii=False), flush=True)


def timestamp(seconds: float) -> str:
    centiseconds = max(0, math.floor(seconds * 100 + 0.5))
    minutes, remainder = divmod(centiseconds, 6000)
    whole_seconds, fraction = divmod(remainder, 100)
    return f"[{minutes:02d}:{whole_seconds:02d}.{fraction:02d}]"


def write_lrc(segments, stream, duration: float, progress=report, on_segment=None, wait=lambda: None) -> int:
    count = 0
    iterator = iter(segments)
    while True:
        wait()
        try:
            segment = next(iterator)
        except StopIteration:
            break
        wait()  # A computation already in flight can finish; retain its result while paused.
        text = " ".join(segment.text.split()).replace("[", "（").replace("]", "）")
        if not text:
            continue
        stream.write(f"{timestamp(segment.start)}{text}\n")
        # An empty timed line clears the overlay in instrumental / silent gaps.
        stream.write(f"{timestamp(segment.end)}\n")
        count += 1
        if on_segment:
            stream.flush()
            on_segment(segment.start, segment.end, text)
        progress(min(99, segment.end / max(duration, 0.01) * 100), text)
    return count


def transcribe_file(model, audio_path, output_path, args, progress=report, on_segment=None, wait=lambda: None):
    wait()
    audio = Path(audio_path).resolve()
    if not audio.is_file():
        raise FileNotFoundError(f"音频文件不存在：{audio}")
    output = Path(output_path).resolve()
    if output == audio:
        raise ValueError("歌词输出不能覆盖音频文件。")
    progress(0, "正在识别，首段结果可能需要一些时间…")
    extra = {"chunk_length": 10} if getattr(args, "stream", False) else {}
    segments, info = model.transcribe(str(audio), language=None if args.language == "auto" else args.language,
                                      task="transcribe", beam_size=5, vad_filter=args.vad,
                                      condition_on_previous_text=False, **extra)
    with output.open("w", encoding="utf-8", newline="\n") as stream:
        title = audio.stem.replace("[", "（").replace("]", "）").replace("\n", " ")
        stream.write(f"[ti:{title}]\n[by:声屿 · 本地识别]\n")
        count = write_lrc(segments, stream, info.duration, progress, on_segment, wait)
    if count == 0:
        output.unlink(missing_ok=True)
        raise RuntimeError("未识别到可用文字；请检查音频、语言设置，歌曲请关闭语音静音过滤。")
    return f"识别完成，共 {count} 段，语言 {info.language}。"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", required=True)
    parser.add_argument("--audio")
    parser.add_argument("--output")
    parser.add_argument("--batch", help="JSON manifest; load the model once for all jobs")
    parser.add_argument("--workers", type=int, choices=range(1, 5), default=1, help="Concurrent file transcription threads")
    parser.add_argument("--stream", action="store_true", help="Emit timed segments before the complete LRC is ready")
    parser.add_argument("--pause-file", help="Pause cooperatively while this signal file exists")
    parser.add_argument("--language", default="auto")
    parser.add_argument("--device", choices=("cpu", "cuda"), default="cpu")
    parser.add_argument("--vad", action="store_true", help="Speech only; leave off for songs")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    model_path = Path(args.model).expanduser().resolve()
    for filename in ("model.bin", "config.json", "tokenizer.json"):
        if not (model_path / filename).is_file():
            raise ValueError(f"本地模型缺少 {filename}：{model_path}。请选择完整的 CTranslate2 / faster-whisper 模型目录。")
    try:
        if args.device == "cuda":
            configure_dll_directories()
        from faster_whisper import WhisperModel
    except ImportError as exc:
        raise RuntimeError("此 Python 尚未安装 faster-whisper，请运行 recognition/setup.ps1 或安装 requirements.txt。") from exc
    report(0, "正在加载本地模型…")
    model = WhisperModel(str(model_path), device=args.device,
                         compute_type="float16" if args.device == "cuda" else "int8",
                         local_files_only=True, num_workers=args.workers,
                         cpu_threads=max(1, min(4, (os.cpu_count() or 4) // args.workers)))
    if args.check:
        report(100, "本地模型加载成功，可以离线识别。")
        return 0
    if args.batch:
        jobs = json.loads(Path(args.batch).read_text(encoding="utf-8-sig"))
        def run_job(job):
            index = job["index"]
            last_percent = 0
            def progress(percent, message):
                nonlocal last_percent
                last_percent = percent
                report(percent, message, index=index, state="running")
            def wait():
                if not args.pause_file or not Path(args.pause_file).exists(): return
                report(last_percent, "已暂停，保留当前识别进度", index=index, state="paused")
                while Path(args.pause_file).exists(): time.sleep(0.1)
                report(last_percent, "继续识别", index=index, state="running")
            def segment(start, end, text):
                report(0, text, index=index, state="segment", start=start, end=end, text=text)
            try:
                message = transcribe_file(model, job["audio"], job["temporary"], args, progress, segment, wait)
                report(100, message, index=index, state="completed")
            except Exception as exc:
                report(100, f"{type(exc).__name__}: {exc}", index=index, state="failed")
        with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers, thread_name_prefix="whisper") as pool:
            list(pool.map(run_job, jobs))
        return 0
    if not args.audio or not args.output:
        parser.error("识别需要 --audio 和 --output")
    def segment(start, end, text):
        report(0, text, type="segment", start=start, end=end, text=text)
    report(100, transcribe_file(model, args.audio, args.output, args, on_segment=segment if args.stream else None))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:
        print(f"{type(exc).__name__}: {exc}", file=sys.stderr, flush=True)
        sys.exit(1)
