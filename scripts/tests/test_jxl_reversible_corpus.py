"""No-codec unit checks for the opt-in real-JPEG corpus probe."""

from __future__ import annotations

from pathlib import Path
import tempfile
import unittest

from scripts.jxl_reversible_corpus import candidate_images, probe_jpeg, jxl_signature


SOURCE = b"\\xff\\xd8\\xff\\xe0" + b"photographic-jpeg" * 24 + b"\\xff\\xd9"
JXL = b"\\xff\\x0a\\x00\\x01"


class CodecDouble:
    def __init__(self, *, mode: str = "valid"):
        self.mode = mode
        self.calls: list[list[str]] = []

    def __call__(self, command: list[str], timeout: int) -> bool:
        self.calls.append(command)
        assert timeout == 3
        if command[0] == "cjxl":
            assert "--lossless_jpeg=1" in command
            assert "--allow_jpeg_reconstruction=1" in command
            if self.mode == "encode-failed":
                return False
            contents = JXL + bytes(len(SOURCE)) if self.mode == "no-size-benefit" else JXL
            if self.mode == "invalid-jxl":
                contents = b"not-jxl"
            Path(command[2]).write_bytes(contents)
            return True

        assert command[0] == "djxl"
        assert "--reconstruct_jpeg" in command
        if self.mode == "decode-failed":
            return False
        restored = SOURCE if self.mode != "mismatch" else SOURCE[:-1] + b"?"
        Path(command[2]).write_bytes(restored)
        return True


class ReversibleCorpusTests(unittest.TestCase):
    def setUp(self):
        self.work = tempfile.TemporaryDirectory()
        self.addCleanup(self.work.cleanup)
        self.root = Path(self.work.name)
        self.jpeg_path = self.root / "cover.jpg"
        self.jpeg_path.write_bytes(SOURCE)

    def test_only_bit_exact_reconstruction_is_verified(self):
        runner = CodecDouble()
        result = probe_jpeg(self.jpeg_path, timeout=3, runner=runner)
        self.assertEqual(result["status"], "verified")
        self.assertEqual(result["jpegBytes"], len(SOURCE))
        self.assertEqual(result["jxlBytes"], len(JXL))
        self.assertEqual(result["savedBytes"], len(SOURCE) - len(JXL))
        self.assertEqual(len(runner.calls), 2)
        self.assertEqual(self.jpeg_path.read_bytes(), SOURCE)

    def test_mismatch_is_never_admitted(self):
        result = probe_jpeg(self.jpeg_path, timeout=3, runner=CodecDouble(mode="mismatch"))
        self.assertEqual(result["status"], "mismatch")

    def test_failed_codecs_and_invalid_output_are_explicit(self):
        for mode, expected in [
            ("encode-failed", "encode-failed"),
            ("decode-failed", "reconstruction-failed"),
            ("invalid-jxl", "invalid-jxl"),
            ("no-size-benefit", "no-size-benefit"),
        ]:
            with self.subTest(mode=mode):
                result = probe_jpeg(
                    self.jpeg_path, timeout=3, runner=CodecDouble(mode=mode)
                )
                self.assertEqual(result["status"], expected)

    def test_non_jpeg_and_oversized_input_never_invoke_codecs(self):
        runner = CodecDouble()
        self.jpeg_path.write_bytes(b"not a jpeg")
        self.assertEqual(
            probe_jpeg(self.jpeg_path, timeout=3, runner=runner)["status"],
            "not-jpeg",
        )
        self.jpeg_path.write_bytes(SOURCE)
        self.assertEqual(
            probe_jpeg(
                self.jpeg_path, timeout=3, runner=runner, max_bytes=12
            )["status"],
            "source-too-large",
        )
        self.assertFalse(runner.calls)

    def test_header_verifies_codestream_and_container(self):
        self.assertTrue(jxl_signature(JXL))
        self.assertTrue(jxl_signature(bytes.fromhex("0000000c4a584c200d0a870a")))
        self.assertFalse(jxl_signature(b"jpeg"))

    def test_sample_enumeration_is_bounded_and_does_not_follow_symlinks(self):
        other = self.root / "other"
        other.mkdir()
        (other / "cover.jpeg").write_bytes(SOURCE)
        (other / "cover.png").write_bytes(SOURCE)
        self.assertEqual(
            len(list(candidate_images(self.root, 1))),
            1,
        )
        self.assertEqual(
            len(list(candidate_images(self.root, 10))),
            2,
        )
        link = self.root / "shortcut.jpg"
        try:
            link.symlink_to(self.jpeg_path)
        except (OSError, NotImplementedError):
            pass
        else:
            self.assertEqual(len(list(candidate_images(self.root, 10))), 2)


if __name__ == "__main__":
    unittest.main()
