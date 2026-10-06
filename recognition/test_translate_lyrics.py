import contextlib
import io
from types import SimpleNamespace
import unittest

from translate_lyrics import translate_lines, detect_language


class Tokenizer:
    def encode(self, text, out_type):
        return list(text)

    def decode(self, tokens):
        return "".join(tokens)


class TranslationTests(unittest.TestCase):
    def test_offline_language_detection_uses_full_lyric_text(self):
        for text, expected in (
            ("The wind carries your voice across the sea. I will wait for you under the stars.", "en"),
            ("这是中文歌词，我们在星空下相遇。", "zh"),
            ("君の声が海を越えて届く。星の下で待っています。", "ja"),
            ("별빛 아래에서 당신을 기다리고 있어요.", "ko"),
        ):
            self.assertEqual(detect_language(["", text, text]), expected)
        with self.assertRaises(ValueError):
            detect_language(["", "1234", "♫"])

    def test_deduplicates_preserves_empty_lines_and_language_prefixes(self):
        calls = []

        def translate(sources, **kwargs):
            calls.append((sources, kwargs))
            self.assertEqual(sources, [["__en__", "A", "</s>"], ["__en__", "B", "</s>"]])
            self.assertEqual(kwargs["target_prefix"], [["__zh__"], ["__zh__"]])
            return [SimpleNamespace(hypotheses=[["__zh__", text, "</s>"]]) for text in ("甲", "乙")]

        with contextlib.redirect_stdout(io.StringIO()):
            result = translate_lines(["A", "", "B", "A", " "], SimpleNamespace(translate_batch=translate), Tokenizer(), "en", "zh")
        self.assertEqual(result, ["甲", "", "乙", "甲", ""])
        self.assertEqual(len(calls), 1)

    def test_long_input_is_rejected_instead_of_silently_truncated(self):
        with self.assertRaisesRegex(ValueError, "1000"):
            translate_lines(["x" * 1001], None, Tokenizer(), "en", "zh")

    def test_empty_or_truncated_hypotheses_are_not_saved(self):
        for tokens in (["__zh__"], ["__zh__"] + ["甲"] * 1023):
            translator = SimpleNamespace(translate_batch=lambda *a, **kw: [SimpleNamespace(hypotheses=[tokens])])
            with self.assertRaises(ValueError):
                translate_lines(["A"], translator, Tokenizer(), "en", "zh")


if __name__ == "__main__":
    unittest.main()
