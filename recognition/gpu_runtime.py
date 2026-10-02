"""Bounded discovery of optional NVIDIA DLLs; shared by inspection and inference."""
import os
from pathlib import Path
import sys

_dll_handles = []

def candidate_directories():
    folders = [Path(sys.executable).parent, Path(sys.executable).parent / "cuda" / "bin"]
    for key, value in os.environ.items():
        if key.startswith("CUDA_PATH") and value:
            folders.extend([Path(value) / "bin", Path(value) / "bin" / "x64"])
    folders.extend(Path(p.strip('"')) for p in os.environ.get("PATH", "").split(os.pathsep) if p)
    program_files = Path(os.environ.get("ProgramFiles", r"C:\Program Files"))
    for pattern in ("NVIDIA GPU Computing Toolkit/CUDA/v*/bin", "NVIDIA GPU Computing Toolkit/CUDA/v*/bin/x64",
                    "NVIDIA/CUDNN/v*/bin", "NVIDIA/CUDNN/v*/bin/*", "NVIDIA GPU Computing Toolkit/cuDNN/v*/bin"):
        folders.extend(program_files.glob(pattern))
    folders.extend((Path(sys.executable).parent / "Lib" / "site-packages" / "nvidia").glob("*/bin"))
    result = []
    seen = set()
    for folder in folders:
        try:
            folder = folder.resolve()
            if folder.is_dir() and str(folder).lower() not in seen:
                seen.add(str(folder).lower()); result.append(folder)
        except (OSError, ValueError):
            continue
    return result

def configure_dll_directories():
    directories = candidate_directories()
    if os.name == "nt":
        # Do not register arbitrary PATH entries: they may contain incompatible OpenMP/CRT DLLs.
        cuda_directories = [p for p in directories if (p / "cublas64_12.dll").is_file() or (p / "cudnn64_9.dll").is_file()]
        for folder in cuda_directories:
            try: _dll_handles.append(os.add_dll_directory(str(folder)))
            except OSError: pass
        # CTranslate2 also uses LoadLibrary, which searches PATH for CUDA libraries.
        os.environ["PATH"] = os.pathsep.join(str(p) for p in cuda_directories) + os.pathsep + os.environ.get("PATH", "")
    return directories
