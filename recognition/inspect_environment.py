"""Read-only local environment inspection. Does not download or install anything."""
import ctypes
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))

from gpu_runtime import configure_dll_directories

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"

def inspect():
    items = []
    def add(name, status, detail): items.append({"name": name, "status": status, "detail": str(detail)})
    add("Python", "ok", f"{platform.python_version()} / {platform.architecture()[0]}\n{sys.executable}")
    dependencies_ok = True
    for package in ("faster-whisper", "ctranslate2", "av", "onnxruntime"):
        try: add(package, "ok", importlib.metadata.version(package))
        except importlib.metadata.PackageNotFoundError:
            dependencies_ok = False; add(package, "error", "未安装")
    try:
        from opencc import OpenCC
        if OpenCC("t2s").convert("繁體中文") != "繁体中文": raise RuntimeError("字典转换自检失败")
        add("OpenCC 繁体转简体", "ok", importlib.metadata.version("opencc-python-reimplemented") + " / 本地字典自检通过")
    except Exception as exc: add("OpenCC 繁体转简体", "warning", str(exc) + "；关闭繁转简后仍可识别。")
    try:
        import sentencepiece
        add("歌词翻译分词器", "ok", "SentencePiece " + importlib.metadata.version("sentencepiece"))
    except Exception as exc: add("歌词翻译分词器", "warning", str(exc) + "；本地翻译需要 SentencePiece，不影响语音识别。")
    try:
        import py3langid
        if py3langid.classify("This is an English sentence.")[0] != "en": raise RuntimeError("原文语言识别自检失败")
        add("原文语言自动识别", "ok", "py3langid " + importlib.metadata.version("py3langid") + " / 本地语言识别自检通过")
    except Exception as exc: add("原文语言自动识别", "warning", str(exc) + "；自动翻译需要随包语言识别资源。")
    directories = configure_dll_directories()
    smi = shutil.which("nvidia-smi")
    if not smi:
        candidate = Path(os.environ.get("WINDIR", r"C:\Windows")) / "System32" / "nvidia-smi.exe"
        smi = str(candidate) if candidate.is_file() else None
    if smi:
        try:
            result = subprocess.run([smi, "--query-gpu=name,driver_version,memory.total", "--format=csv,noheader"], capture_output=True, text=True, timeout=5, creationflags=0x08000000)
            add("NVIDIA GPU / 驱动", "ok" if result.returncode == 0 else "warning", result.stdout.strip() or result.stderr.strip())
        except (OSError, subprocess.TimeoutExpired) as exc: add("NVIDIA GPU / 驱动", "warning", str(exc))
    else: add("NVIDIA GPU / 驱动", "warning", "未找到 nvidia-smi；继续检查驱动和运行库。")
    nvcc = shutil.which("nvcc")
    if nvcc:
        try:
            result = subprocess.run([nvcc, "--version"], capture_output=True, text=True, timeout=5, creationflags=0x08000000)
            add("CUDA Toolkit 编译器", "info", result.stdout.strip() or result.stderr.strip())
        except (OSError, subprocess.TimeoutExpired) as exc: add("CUDA Toolkit 编译器", "warning", str(exc))
    else: add("CUDA Toolkit 编译器", "info", "未发现 nvcc；推理只需兼容的 CUDA 运行库，不必安装编译器。")
    libraries = {}
    for dll, label in (("nvcuda.dll", "CUDA 驱动"), ("cublas64_12.dll", "cuBLAS 12"), ("cublasLt64_12.dll", "cuBLAS Lt 12"), ("cudnn64_9.dll", "cuDNN 9")):
        found = next((str(p / dll) for p in directories if (p / dll).is_file()), dll)
        try:
            lib = ctypes.WinDLL(found)
            libraries[dll] = lib
            location = ctypes.create_unicode_buffer(32768)
            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel.GetModuleFileNameW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_uint]
            kernel.GetModuleFileNameW(lib._handle, location, len(location))
            detail = location.value or found
            if dll == "nvcuda.dll":
                version = ctypes.c_int()
                lib.cuDriverGetVersion(ctypes.byref(version))
                detail += f"\n驱动支持 CUDA {version.value // 1000}.{version.value % 1000 // 10}（不等于已安装 Toolkit）"
            if dll == "cudnn64_9.dll":
                lib.cudnnGetVersion.restype = ctypes.c_size_t
                detail += f"\ncuDNN version code: {lib.cudnnGetVersion()}"
            add(label, "ok", detail)
        except (OSError, AttributeError) as exc: add(label, "warning", f"未找到或无法加载 {dll}：{exc}")
    cuda_device = False
    try:
        import ctranslate2
        device_count = ctranslate2.get_cuda_device_count()
        if device_count:
            types = sorted(ctranslate2.get_supported_compute_types("cuda"))
            cuda_device = "float16" in types
            add("CTranslate2 GPU", "ok" if cuda_device else "warning", f"可见设备：{device_count}；计算类型：{', '.join(types)}")
        else: add("CTranslate2 GPU", "warning", "未发现可用 CUDA 设备，将使用 CPU。")
    except Exception as exc: add("CTranslate2 GPU", "warning", str(exc))
    cpu_ready = False
    try:
        import faster_whisper
        import av
        import onnxruntime
        import ctranslate2
        cpu_ready = "int8" in ctranslate2.get_supported_compute_types("cpu")
        add("CPU 离线识别", "ok" if cpu_ready else "warning", "依赖加载成功，支持 INT8。" if cpu_ready else "此 CPU 不支持 INT8。")
    except Exception as exc: add("CPU 离线识别", "error", str(exc))
    cuda_ready = dependencies_ok and cuda_device and len(libraries) == 4
    return {"items": items, "cpuReady": cpu_ready, "cudaReady": cuda_ready,
            "summary": "CUDA 基础依赖已就绪；请再检查选中模型以确认推理。" if cuda_ready else ("CUDA 依赖不完整或不可用；可使用 CPU 离线识别。" if cpu_ready else "CPU 识别依赖未通过检查，请查看下方诊断。")}

if __name__ == "__main__":
    print(json.dumps(inspect(), ensure_ascii=False), flush=True)
