"""Bilibili plugin worker. Request (including scoped cookies) is read only from stdin.
Stdout is sanitized JSONL. yt-dlp handles Bilibili's changing signed playback API.
"""
from __future__ import annotations
import http.cookiejar
import json
import os
from pathlib import Path
import re
import sys
import time
import urllib.request
import urllib.parse

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE / "vendor"))
# Development keeps heavy redistributables outside source control.
if not (HERE / "vendor" / "yt_dlp").exists():
    for parent in HERE.parents:
        if (parent / "runtime/bilibili/vendor/yt_dlp").exists():
            sys.path.insert(0, str(parent / "runtime/bilibili/vendor"))
            break

def emit(kind, **values):
    print(json.dumps({"type": kind, **values}, ensure_ascii=False), flush=True)

def normalize_bv(value):
    value = value.strip()
    if re.fullmatch(r"BV[0-9A-Za-z]{10}", value):
        return value
    match = re.fullmatch(r"https://(?:www\.)?bilibili\.com/video/(BV[0-9A-Za-z]{10})/?(?:[?#].*)?", value)
    if match:
        return match[1]
    raise ValueError("请输入有效的 BV 号或 https://www.bilibili.com/video/BV… 链接。")

def cookie_jar(cookies):
    jar = http.cookiejar.CookieJar()
    for c in cookies:
        domain = c.get("domain", "").lower()
        if domain.lstrip(".") != "bilibili.com" and not domain.endswith(".bilibili.com"):
            continue
        name, value = c.get("name", ""), c.get("value", "")
        if not name or any(ch in name + value for ch in "\r\n"):
            continue
        expires = c.get("expires")
        if expires and expires <= time.time():
            continue
        jar.set_cookie(http.cookiejar.Cookie(0, name, value, None, False, domain, domain.startswith("."),
            domain.startswith("."), c.get("path") or "/", True, c.get("secure", True), int(expires) if expires else None,
            not bool(expires), None, None, {}, False))
    return jar

def api(path, cookies):
    # No user-provided hosts. CookieJar scopes credentials across redirects.
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cookie_jar(cookies)))
    request = urllib.request.Request("https://api.bilibili.com/" + path, headers={
        "User-Agent": "Mozilla/5.0", "Referer": "https://www.bilibili.com/"})
    with opener.open(request, timeout=25) as response:
        result = json.load(response)
    if result.get("code") != 0:
        code = result.get("code")
        raise RuntimeError(f"Bilibili 接口返回 {code}，请检查登录、视频可用性或稍后重试。")
    return result["data"]

def safe_message(message, cookies=()):
    text = str(message)
    for c in cookies:
        if c.get("value"):
            text = text.replace(c["value"], "[凭据已隐藏]")
    text = re.sub(r"https?://\S+", "[链接]", text)
    return text[:1200]

class QuietLogger:
    def __init__(self, cookies): self.cookies = cookies
    def debug(self, message): pass
    def info(self, message): pass
    def warning(self, message): emit("warning", message=safe_message(message, self.cookies))
    def error(self, message): pass  # One sanitized failure is emitted by the caller.

def make_ydl(cookies, **options):
    from yt_dlp import YoutubeDL
    ydl = YoutubeDL({"quiet": True, "no_warnings": False, "logger": QuietLogger(cookies),
        "noplaylist": True, "cachedir": False, "socket_timeout": 25, "retries": 3,
        "fragment_retries": 3, "extractor_retries": 2, "http_headers": {"Referer": "https://www.bilibili.com/"},
        **options})
    for cookie in cookie_jar(cookies):
        ydl.cookiejar.set_cookie(cookie)
    return ydl

def quality_options(formats):
    choices = {}
    for fmt in formats:
        q = fmt.get("quality")
        if q is not None and q > 0 and fmt.get("vcodec") != "none":
            q = int(q)
            choices[q] = {"id": q, "label": fmt.get("format") or fmt.get("format_note") or f'{fmt.get("height", "")}P / {q}'}
    return [choices[key] for key in sorted(choices, reverse=True)]

def format_selector(video, quality):
    if not video:
        return "bestaudio[acodec^=mp4a]/bestaudio"
    condition = f"[quality={int(quality)}]" if quality else ""
    # Never silently lower a user-selected quality on another episode.
    return f"bestvideo{condition}[vcodec^=avc]+bestaudio[acodec^=mp4a]/bestvideo{condition}+bestaudio/best{condition}"

def filename(text):
    return re.sub(r'[<>:"/\\|?*\x00-\x1f]', '_', text).strip(' .')[:80] or "Bilibili"

def srt_to_lrc(text):
    lines = []
    for block in re.split(r"\r?\n\s*\r?\n", text.strip()):
        match = re.search(r"(\d+):(\d+):(\d+)[,.](\d+)\s+-->\s+(\d+):(\d+):(\d+)[,.](\d+)\s*\r?\n(.+)", block, re.S)
        if not match: continue
        groups = match.groups()
        def stamp(values):
            h, m, s, fraction = values
            centiseconds = (int(h) * 3600 + int(m) * 60 + int(s)) * 100 + int((fraction + '00')[:2])
            return f"[{centiseconds // 6000:02d}:{centiseconds // 100 % 60:02d}.{centiseconds % 100:02d}]"
        content = re.sub(r'<[^>]+>', '', ' '.join(groups[8].split())).replace('[', '（').replace(']', '）')
        lines.extend([stamp(groups[:4]) + content, stamp(groups[4:8])])
    return '\n'.join(lines) + '\n' if lines else ''

def login_status(code):
    return {86101: "等待扫码，请使用 Bilibili App 扫一扫。", 86090: "已扫码，请在手机上确认登录。", 86038: "二维码已失效，请刷新后重新扫码。", 0: "登录成功"}.get(code)

def qr_login():
    import qrcode
    jar = http.cookiejar.CookieJar()
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
    headers = {"User-Agent": "Mozilla/5.0", "Referer": "https://www.bilibili.com/", "Origin": "https://passport.bilibili.com"}
    def passport(path):
        with opener.open(urllib.request.Request("https://passport.bilibili.com/x/passport-login/web/qrcode/" + path, headers=headers), timeout=20) as response:
            result = json.load(response)
        if result.get("code") != 0: raise RuntimeError(f'扫码登录接口返回 {result.get("code")}，请稍后重试。')
        return result["data"]
    generated = passport("generate")
    url = urllib.parse.urlparse(generated["url"])
    if url.scheme != "https" or not (url.hostname == "bilibili.com" or (url.hostname or "").endswith(".bilibili.com")):
        raise RuntimeError("登录接口返回了无效的二维码地址。")
    code = qrcode.QRCode(error_correction=qrcode.constants.ERROR_CORRECT_M, border=4)
    code.add_data(generated["url"]); code.make(fit=True)
    emit("qr", matrix=code.get_matrix())
    deadline = time.monotonic() + 180
    previous = None
    errors = 0
    while time.monotonic() < deadline:
        time.sleep(2)
        try:
            result = passport("poll?" + urllib.parse.urlencode({"qrcode_key": generated["qrcode_key"]}))
            errors = 0
        except (OSError, TimeoutError):
            errors += 1
            if errors >= 5: raise RuntimeError("登录状态查询失败，请检查网络后刷新二维码。") from None
            continue
        status = result.get("code")
        if status == 0:
            # CookieJar parses each Set-Cookie header, including commas in Expires, correctly.
            cookies = [{"name": c.name, "value": c.value, "domain": c.domain, "path": c.path, "secure": c.secure, "expires": c.expires} for c in jar
                if c.domain.lstrip('.') == 'bilibili.com' or c.domain.endswith('.bilibili.com')]
            if not any(c['name'] == 'SESSDATA' for c in cookies): raise RuntimeError("登录接口未返回完整凭据，请重新扫码。")
            account = api("x/web-interface/nav", cookies)
            if not account.get("isLogin"): raise RuntimeError("登录尚未生效，请重新扫码确认。")
            emit("login", name=account.get("uname", "Bilibili 用户"), cookies=cookies)
            return
        message = login_status(status)
        if status == 86038: raise RuntimeError(message)
        if message is None: raise RuntimeError(f"未知扫码状态 {status}，请刷新重试。")
        if previous != status: emit("login_status", message=message)
        previous = status
    raise RuntimeError("等待扫码超时，请刷新二维码后重试。")

def inspect(request):
    bv = normalize_bv(request["bv"])
    cookies = request.get("cookies", [])
    data = api("x/web-interface/view?bvid=" + bv, cookies)
    pages = [{"index": int(p["page"]), "cid": p["cid"], "title": p["part"], "duration": p["duration"]} for p in data["pages"]]
    emit("catalog", bv=bv, title=data["title"], pages=pages)
    formats(request | {"bv": bv, "page": pages[0]["index"]})

def formats(request):
    bv = normalize_bv(request["bv"])
    page = max(1, int(request.get("page", 1)))
    with make_ydl(request.get("cookies", []), skip_download=True) as ydl:
        info = ydl.extract_info(f"https://www.bilibili.com/video/{bv}?p={page}", download=False)
        emit("formats", page=page, qualities=quality_options(info.get("formats", [])))

def download(request):
    cookies = request.get("cookies", [])
    bv = normalize_bv(request["bv"])
    pages = request["pages"]
    if not pages or len(pages) > 1000 or any(int(p["index"]) < 1 for p in pages): raise ValueError("请勾选有效分 P。")
    video = bool(request.get("video", False))
    quality = int(request.get("quality", 0))
    ffmpeg = Path(request["ffmpeg"])
    if not (ffmpeg / "ffmpeg.exe").is_file(): raise FileNotFoundError("插件缺少 FFmpeg，请重新解压完整交付包。")
    output = Path(request["output"]).resolve()
    output.mkdir(parents=True, exist_ok=True)
    completed, failed, skipped = 0, 0, 0
    for page in pages:
        index = int(page["index"])
        base = f'{filename(page["title"])} [{bv}] P{index:03d}' + (f" q{quality or 'best'}" if video else " audio")
        extension = ".mp4" if video else ".m4a"
        target = output / (base + extension)
        if target.exists():
            skipped += 1
            emit("item", index=index, state="skipped", message="已跳过：同名文件已存在", path=str(target))
            continue
        work = output / f".shengyu-{bv}-{index}-{'video' if video else 'audio'}-{quality}"
        work.mkdir(exist_ok=True)
        last_update = 0
        def hook(progress):
            nonlocal last_update
            now = time.monotonic()
            if now - last_update < 0.35 and progress["status"] == "downloading": return
            last_update = now
            total = progress.get("total_bytes") or progress.get("total_bytes_estimate") or 0
            percent = min(99, progress.get("downloaded_bytes", 0) / total * 100) if total else 0
            emit("progress", index=index, percent=percent, message="正在下载" if progress["status"] == "downloading" else "下载流完成，正在处理…")
        try:
            emit("item", index=index, state="running", message="正在解析分 P…")
            options = {"format": format_selector(video, quality), "outtmpl": str(work / "media.%(ext)s"),
                "ffmpeg_location": str(ffmpeg), "merge_output_format": "mp4", "windowsfilenames": True,
                "progress_hooks": [hook], "postprocessor_hooks": [lambda _: emit("progress", index=index, percent=99, message="FFmpeg 正在处理…")],
                "writesubtitles": bool(request.get("subtitles")), "subtitleslangs": ["all", "-danmaku"], "subtitlesformat": "srt",
                "postprocessors": [] if video else [{"key": "FFmpegExtractAudio", "preferredcodec": "m4a"}]}
            with make_ydl(cookies, **options) as ydl:
                info = ydl.extract_info(f"https://www.bilibili.com/video/{bv}?p={index}", download=True)
            media = work / ("media" + extension)
            if not media.is_file() or media.stat().st_size == 0: raise RuntimeError("下载未生成完整音视频文件。")
            # Windows rename fails if destination exists, protecting an externally-created file.
            media.rename(target)
            subtitles = sorted(work.glob("media.*.srt"), key=lambda p: (0 if '.zh' in p.name else 1, p.name))
            for subtitle in subtitles:
                destination = output / (base + subtitle.name[len("media"):])
                if not destination.exists(): subtitle.rename(destination)
            if subtitles:
                first = output / (base + subtitles[0].name[len("media"):])
                lrc = srt_to_lrc(first.read_text(encoding="utf-8-sig"))
                if lrc:
                    try:
                        with (output / (base + '.lrc')).open('x', encoding='utf-8') as handle: handle.write(lrc)
                    except FileExistsError: pass
            note = "下载完成" + (" · 未获取字幕，请检查登录或该分 P 是否提供字幕" if request.get("subtitles") and not subtitles else "")
            completed += 1
            emit("item", index=index, state="completed", message=note, path=str(target))
            # Remove only our empty staging folder, never the user's download directory.
            try: work.rmdir()
            except OSError: pass
        except Exception as exc:
            failed += 1
            emit("item", index=index, state="failed", message=safe_message(exc, cookies))
    emit("complete", completed=completed, failed=failed, skipped=skipped)

def main():
    # Match WorkerClient's UTF-8 pipes even when invoked directly with a legacy Windows code page.
    sys.stdin.reconfigure(encoding="utf-8", errors="strict")
    sys.stdout.reconfigure(encoding="utf-8", errors="strict", write_through=True)
    sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace", write_through=True)
    request = json.load(sys.stdin)
    action = request.get("action")
    try:
        if action == "login": qr_login()
        elif action == "account":
            data = api("x/web-interface/nav", request.get("cookies", []))
            emit("account", loggedIn=data.get("isLogin", False), name=data.get("uname", ""))
        elif action == "inspect": inspect(request)
        elif action == "formats": formats(request)
        elif action == "download": download(request)
        else: raise ValueError("未知插件操作")
        return 0
    except Exception as exc:
        emit("error", message=safe_message(exc, request.get("cookies", [])))
        return 1

if __name__ == "__main__":
    sys.exit(main())
