import io
from email.message import Message
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import worker

class WorkerTests(unittest.TestCase):
    def test_protocol_survives_legacy_windows_pipe_encodings(self):
        sample = '扫码登录 · 测试用户 · 繁體字幕 · 音乐🎵 · 日本語 · 한국어'
        script = '''
import sys
sys.path.insert(0, sys.argv[1])
import worker
for stream in (sys.stdin, sys.stdout, sys.stderr):
    stream.reconfigure(encoding=sys.argv[2], errors='strict')
worker.api = lambda path, cookies: {'isLogin': True, 'uname': cookies[0]['value']}
sys.exit(worker.main())
'''
        request = json.dumps({'action': 'account', 'cookies': [{'value': sample}]}, ensure_ascii=False).encode('utf-8')
        for encoding in ('cp936', 'cp1252'):
            with self.subTest(encoding=encoding):
                result = subprocess.run([sys.executable, '-I', '-X', 'utf8=0', '-c', script, str(Path(worker.__file__).parent), encoding],
                                        input=request, capture_output=True, timeout=15)
                self.assertEqual(result.returncode, 0, result.stderr.decode('utf-8', errors='replace'))
                event = json.loads(result.stdout.decode('utf-8'))
                self.assertEqual(event, {'type': 'account', 'loggedIn': True, 'name': sample})

    def test_qr_login_cookie_headers_and_phone_confirmation(self):
        events = []
        responses = iter([{'url': 'https://passport.bilibili.com/test-qr', 'qrcode_key': 'test-key'}, {'code': 86101}, {'code': 86090}, {'code': 0}])
        def opener_factory(handler):
            class Opener:
                def open(self, request, timeout):
                    data = next(responses)
                    if data.get('code') == 0:
                        headers = Message()
                        headers.add_header('Set-Cookie', 'SESSDATA=test-session; Domain=.bilibili.com; Path=/; Expires=Wed, 01 Dec 2027 10:00:00 GMT; Secure; HttpOnly')
                        headers.add_header('Set-Cookie', 'DedeUserID=12345; Domain=.bilibili.com; Path=/')
                        handler.cookiejar.extract_cookies(mock.Mock(info=lambda: headers), request)
                    return io.BytesIO(json.dumps({'code': 0, 'data': data}).encode())
            return Opener()
        with mock.patch.object(worker.urllib.request, 'build_opener', side_effect=opener_factory), mock.patch.object(worker.time, 'sleep'), mock.patch.object(worker, 'api', return_value={'isLogin': True, 'uname': '测试用户'}), mock.patch.object(worker, 'emit', side_effect=lambda kind, **data: events.append((kind, data))):
            worker.qr_login()
        self.assertEqual([event[0] for event in events], ['qr', 'login_status', 'login_status', 'login'])
        self.assertEqual({c['name'] for c in events[-1][1]['cookies']}, {'SESSDATA', 'DedeUserID'})
        self.assertEqual(events[-1][1]['name'], '测试用户')

    def test_qr_expiry_does_not_save_session(self):
        responses = iter([{'url': 'https://passport.bilibili.com/test-qr', 'qrcode_key': 'test-key'}, {'code': 86038}])
        opener = mock.Mock()
        opener.open.side_effect = lambda *a, **k: io.BytesIO(json.dumps({'code': 0, 'data': next(responses)}).encode())
        with mock.patch.object(worker.urllib.request, 'build_opener', return_value=opener), mock.patch.object(worker.time, 'sleep'), mock.patch.object(worker, 'emit') as emit:
            with self.assertRaisesRegex(RuntimeError, '失效'): worker.qr_login()
        self.assertFalse(any(call.args[0] == 'login' for call in emit.call_args_list))

    def test_bv_rejects_foreign_hosts_and_command_inputs(self):
        self.assertEqual(worker.normalize_bv('https://www.bilibili.com/video/BV13x41117TL/?p=2'), 'BV13x41117TL')
        for bad in ('https://evil.test/video/BV13x41117TL', 'BV13x41117TL;calc', '../BV13x41117TL', 'BV1'):
            with self.assertRaises(ValueError): worker.normalize_bv(bad)

    def test_credentials_are_scoped_and_redacted(self):
        cookies = [{'name': 'SESSDATA', 'value': 'secret-session', 'domain': '.bilibili.com'},
                   {'name': 'wrong', 'value': 'bad', 'domain': 'notbilibili.com'},
                   {'name': 'old', 'value': 'expired', 'domain': '.bilibili.com', 'expires': 1}]
        self.assertEqual([c.name for c in worker.cookie_jar(cookies)], ['SESSDATA'])
        text = worker.safe_message('secret-session https://api.bilibili.com/?token=secret-session', cookies)
        self.assertNotIn('secret-session', text)
        self.assertNotIn('token=', text)

    def test_quality_is_exact_and_audio_does_not_request_video(self):
        self.assertEqual(worker.format_selector(False, 80), 'bestaudio[acodec^=mp4a]/bestaudio')
        self.assertEqual(worker.format_selector(True, 112).count('[quality=112]'), 3)
        formats = [{'quality': 80, 'vcodec': 'avc', 'format': '1080P'}, {'quality': 80, 'vcodec': 'hevc'}, {'quality': 64, 'vcodec': 'avc', 'format': '720P'}, {'vcodec': 'none'}]
        self.assertEqual([q['id'] for q in worker.quality_options(formats)], [80, 64])

    def test_subtitle_conversion_timing_and_silence(self):
        lrc = worker.srt_to_lrc('1\n01:02:03,450 --> 01:02:04,000\n<b>你好</b>\n世界\n\n2\n01:02:05,000 --> 01:02:06,500\nagain')
        self.assertEqual(lrc, '[62:03.45]你好 世界\n[62:04.00]\n[62:05.00]again\n[62:06.50]\n')

    def test_selected_pages_outputs_subtitles_skips_and_failures(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); ffmpeg = root / 'ffmpeg'; ffmpeg.mkdir(); (ffmpeg / 'ffmpeg.exe').touch()
            calls, events = [], []
            class FakeYdl:
                def __init__(self, options): self.options = options
                def __enter__(self): return self
                def __exit__(self, *args): pass
                def extract_info(self, url, download):
                    calls.append(url)
                    if '?p=3' in url: raise RuntimeError('one episode unavailable')
                    base = Path(self.options['outtmpl']).parent
                    (base / 'media.m4a').write_bytes(b'audio')
                    (base / 'media.zh-Hans.srt').write_text('1\n00:00:01,230 --> 00:00:02,000\n字幕\n', encoding='utf-8')
                    return {}
            output = root / '下载目录 音乐🎵'
            request = {'bv': 'BV13x41117TL', 'pages': [{'index': 1, 'title': '第一集 繁體🎵'}, {'index': 3, 'title': '第三集'}], 'output': str(output), 'ffmpeg': str(ffmpeg), 'subtitles': True}
            with mock.patch.object(worker, 'make_ydl', side_effect=lambda cookies, **opts: FakeYdl(opts)), mock.patch.object(worker, 'emit', side_effect=lambda kind, **data: events.append((kind, data))):
                worker.download(request)
                worker.download(request)
            self.assertEqual([p.name for p in output.glob('*.m4a')], ['第一集 繁體🎵 [BV13x41117TL] P001 audio.m4a'])
            self.assertEqual(next(output.glob('*.lrc')).read_text(encoding='utf-8'), '[00:01.23]字幕\n[00:02.00]\n')
            self.assertEqual(next(output.glob('*.srt')).read_text(encoding='utf-8'), '1\n00:00:01,230 --> 00:00:02,000\n字幕\n')
            self.assertFalse(any('?p=2' in url for url in calls))
            self.assertEqual(events[-1], ('complete', {'completed': 0, 'failed': 1, 'skipped': 1}))

if __name__ == '__main__': unittest.main()
