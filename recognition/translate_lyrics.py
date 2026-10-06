"""Offline M2M100 lyric translation. UTF-8 JSON IPC; never downloads or uploads text."""
import argparse
import json
import os
from pathlib import Path
import sys

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"


def emit(kind, **values):
    print(json.dumps({"type": kind, **values}, ensure_ascii=False), flush=True)


def detect_language(lines):
    import py3langid
    # A whole-song sample is more reliable than guessing from one short lyric.
    sample = "\n".join(dict.fromkeys(line.strip() for line in lines if line.strip()))[:30000]
    if not any(char.isalpha() for char in sample):
        raise ValueError("歌词没有足够的语言文本，无法自动识别原文语言。")
    language, _ = py3langid.classify(sample)
    return {"nso": "ns", "fuv": "ff", "nn": "no", "uzs": "uz"}.get(language, language)


def translate_lines(lines, translator, tokenizer, source, target):
    """Translate distinct nonempty lines in small batches without changing their ordering."""
    unique = list(dict.fromkeys(line for line in lines if line.strip()))
    results = {line: "" for line in lines if not line.strip()}
    for offset in range(0, len(unique), 4):
        batch = unique[offset:offset + 4]
        sources = []
        for line in batch:
            pieces = tokenizer.encode(line, out_type=str)
            # Refuse oversized lines instead of silently truncating a lyric/chapter.
            if len(pieces) > 1000:
                raise ValueError("歌词单行过长（超过 1000 个分词），请先拆分这一行再翻译。")
            sources.append([f"__{source}__", *pieces, "</s>"])
        hypotheses = translator.translate_batch(sources, target_prefix=[[f"__{target}__"] for _ in batch],
            beam_size=4, max_input_length=1024, max_decoding_length=1024)
        for line, hypothesis in zip(batch, hypotheses, strict=True):
            tokens = hypothesis.hypotheses[0]
            if len(tokens) >= 1024:
                raise ValueError("译文达到模型长度限制，请拆分长行后重试。")
            decoded = tokenizer.decode([token for token in tokens if token not in (f"__{target}__", "</s>", "<s>", "<pad>")]).strip()
            if not decoded:
                raise ValueError("模型未生成有效译文；请检查原文语言设置。")
            results[line] = decoded
        emit("progress", percent=min(99, (offset + len(batch)) / max(1, len(unique)) * 100),
             message=f"已翻译 {min(offset + len(batch), len(unique))} / {len(unique)} 行（重复歌词复用译文）")
    return [results[line] for line in lines]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument("--target", required=True)
    parser.add_argument("--device", choices=("cpu", "cuda"), default="cpu")
    args = parser.parse_args()
    model = Path(args.model).resolve(strict=True)
    lines = json.load(sys.stdin)
    if not isinstance(lines, list) or not lines or any(not isinstance(line, str) for line in lines):
        raise ValueError("无效的歌词输入。")
    vocab_file = model / "shared_vocabulary.txt"
    if vocab_file.exists():
        vocabulary = set(vocab_file.read_text(encoding="utf-8").splitlines())
    else:
        vocabulary = set(json.loads((model / "shared_vocabulary.json").read_text(encoding="utf-8")))
    source = detect_language(lines) if args.source == "auto" else args.source
    emit("progress", percent=0, message=f"自动识别原文语言：{source} · 目标语言：{args.target}")
    if any(f"__{lang}__" not in vocabulary for lang in (source, args.target)):
        raise ValueError(f"模型不支持识别出的原文语言（{source}）或所选目标语言。")
    emit("progress", percent=0, message="正在加载本地翻译模型…")
    if args.device == "cuda":
        # Embedded Python does not add the script directory in isolated mode.
        sys.path.insert(0, str(Path(__file__).resolve().parent))
        from gpu_runtime import configure_dll_directories
        configure_dll_directories()
    import ctranslate2
    import sentencepiece
    tokenizer = sentencepiece.SentencePieceProcessor(model_file=str(model / "sentencepiece.bpe.model"))
    if source == args.target:
        result = lines
    else:
        translator = ctranslate2.Translator(str(model), device=args.device, compute_type="int8" if args.device == "cpu" else "float16",
                                          inter_threads=1, intra_threads=min(4, os.cpu_count() or 1))
        result = translate_lines(lines, translator, tokenizer, source, args.target)
    if args.target == "zh":
        from opencc import OpenCC
        converter = OpenCC("t2s")
        result = [converter.convert(line) for line in result]
    for index, line in enumerate(result):
        emit("line", index=index, text=line)
    emit("progress", percent=100, message="翻译完成，正在保存 LRC…")


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"{type(exc).__name__}: {exc}", file=sys.stderr, flush=True)
        sys.exit(1)
