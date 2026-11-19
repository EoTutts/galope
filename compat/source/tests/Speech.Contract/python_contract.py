"""Regression tests for the Python 3.8 speech adapter (no audio device)."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = Path(os.environ.get("SPEECH_SOURCE", str(ROOT / "vendor/r36s-a11y-sdk/python/r36s_a11y/speech.py")))
spec = importlib.util.spec_from_file_location("speech", SOURCE)
speech = importlib.util.module_from_spec(spec)
spec.loader.exec_module(speech)


class Sink:
    def __init__(self): self.data = b""
    def sendall(self, data): self.data += data
    def close(self): pass


class Transport(speech.Speech):
    def __init__(self):
        super().__init__()
        self.commands = []
    def _connect(self):
        self.sock = Sink()
        self._apply_voice(speech._user_voice())
    def _cmd(self, cmd):
        self.commands.append(cmd)
        return ["200 OK"]
    def _reply(self): return ["225-1", "225 OK MESSAGE QUEUED"]


class Contract(unittest.TestCase):
    def setUp(self):
        self.old = dict(os.environ)
        self.tmp = tempfile.TemporaryDirectory()
        self.config = Path(self.tmp.name) / "config.json"
        os.environ.pop("A11Y_VOZ_CONF", None)
        os.environ["A11Y_CONFIG"] = str(self.config)
        os.environ["A11Y_FALA_DESLIGADA"] = str(Path(self.tmp.name) / "mute")
        self.config.write_text(json.dumps({"voice": {"VOICE_MODULE": "rhvoice", "VOICE": "Letícia", "RATE": 62, "PITCH": 40, "VOLUME": 90}, "system": {"VOICE": "wrong"}}), encoding="utf-8")
    def tearDown(self):
        os.environ.clear(); os.environ.update(self.old)
        self.tmp.cleanup()
    def test_compact_json_and_unicode(self):
        self.assertEqual(speech._user_voice(), {"VOICE_MODULE": "rhvoice", "VOICE": "Letícia", "RATE": 24, "PITCH": -20, "VOLUME": 80})
    def test_legacy_override_and_clamp(self):
        old = Path(self.tmp.name) / "voz.conf"
        old.write_text('VOICE="Pedro"\nRATE=200\nPITCH=-30\n', encoding="utf-8")
        os.environ["A11Y_VOZ_CONF"] = str(old)
        self.assertEqual(speech._user_voice(), {"VOICE": "Pedro", "RATE": 100, "PITCH": -100})
    def test_malformed_or_wrong_voice_section(self):
        for text in ('{"voice":', '{"voice":null}', '{"system":{"VOICE":"wrong"}}'):
            self.config.write_text(text, encoding="utf-8")
            self.assertEqual(speech._user_voice(), {})
    def test_actual_ssip_commands_and_live_update(self):
        client = Transport()
        client.say("Primeira")
        self.assertIn("SET SELF SYNTHESIS_VOICE Letícia", client.commands)
        self.assertIn("SET SELF RATE 24", client.commands)
        self.config.write_text('{"voice":{"VOICE_MODULE":"rhvoice","VOICE":"Pedro","RATE":75}}', encoding="utf-8")
        client.say("Segunda", queue=True)
        self.assertIn("SET SELF SYNTHESIS_VOICE Pedro", client.commands)
        self.assertIn("SET SELF RATE 50", client.commands)
        self.assertEqual(client.commands.count("STOP SELF"), 1)
        self.assertIn(b"Segunda\r\n.\r\n", client.sock.data)
    def test_mute_completes_without_sending(self):
        Path(os.environ["A11Y_FALA_DESLIGADA"]).touch()
        client = Transport()
        done = []
        client.say("Silêncio", on_done=done.append)
        self.assertEqual(client.commands, [])
        for fn, value in client.poll(): fn(value)
        self.assertEqual(done, [True])
    def test_callbacks_and_cancel_are_client_local(self):
        client = Transport()
        done = []
        client.say("Mensagem", on_done=done.append)
        client._handle_event(["702-1", "702-10", "702 END"])
        for fn, value in client.events: fn(value)
        self.assertEqual(done, [True])
        client.hush()
        self.assertEqual(client.commands[-1], "STOP SELF")
    def test_dot_stuffing(self):
        self.assertEqual(speech.escape_text(".\nTeste"), "..\r\nTeste\r\n.\r\n")


if __name__ == "__main__": unittest.main()
