"""Private audio routes for the real-client launcher; no game or desktop mutation."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("caverarria_launcher", Path(__file__).resolve().parents[2] / "scripts/run.py")
LAUNCHER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(LAUNCHER)


class LauncherAudioTests(unittest.TestCase):
    def test_headless_is_silent_even_when_desktop_driver_is_inherited(self):
        inherited = {"SDL_AUDIODRIVER": "pulseaudio", "CAVERARRIA_AUDIO_DEVICE": "pulse", "PATH": "/example"}
        result = LAUNCHER.audio_test_environment(inherited, True)
        self.assertEqual("dummy", result["SDL_AUDIODRIVER"])
        self.assertNotIn("CAVERARRIA_AUDIO_DEVICE", result)
        self.assertEqual("/example", result["PATH"])
        self.assertEqual("pulseaudio", inherited["SDL_AUDIODRIVER"])

    def test_capture_uses_private_disk_route(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "mixed.raw"
            result = LAUNCHER.audio_test_environment({"SDL_AUDIODRIVER": "pulse", "SDL_DISKAUDIOFILEIN": "stale.raw"}, True, output)
            self.assertEqual("disk", result["SDL_AUDIODRIVER"])
            self.assertEqual(str(output.resolve()), result["SDL_DISKAUDIOFILE"])
            self.assertNotIn("SDL_DISKAUDIOFILEIN", result)
            self.assertFalse(output.exists())

    def test_dummy_does_not_keep_previous_capture(self):
        result = LAUNCHER.audio_test_environment({"SDL_DISKAUDIOFILE": "stale.raw"}, True)
        self.assertNotIn("SDL_DISKAUDIOFILE", result)

    def test_interactive_audio_environment_is_preserved(self):
        inherited = {"SDL_AUDIODRIVER": "pulseaudio", "CAVERARRIA_AUDIO_DEVICE": "pulse"}
        self.assertEqual(inherited, LAUNCHER.audio_test_environment(inherited, False))
        with self.assertRaisesRegex(ValueError, "requires --headless"):
            LAUNCHER.audio_test_environment(inherited, False, Path("capture.raw"))


if __name__ == "__main__":
    unittest.main()
