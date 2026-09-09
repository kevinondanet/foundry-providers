"""Acceptance tests for configkit.load_config."""

import tempfile
import unittest
from pathlib import Path

from configkit import Config, ConfigError, load_config

TOML = '''
[server]
host = "127.0.0.1"
port = 8080

[logging]
level = "INFO"
'''


class TestLoadConfig(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.path = Path(self.tmp.name) / "config.toml"
        self.path.write_text(TOML, encoding="utf-8")

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def test_loads_sections_into_dataclass(self) -> None:
        cfg = load_config(self.path, env={})

        self.assertIsInstance(cfg, Config)
        self.assertEqual(cfg.server.host, "127.0.0.1")
        self.assertEqual(cfg.server.port, 8080)
        self.assertEqual(cfg.logging.level, "INFO")

    def test_environment_overrides_with_type_coercion(self) -> None:
        cfg = load_config(self.path, env={"APP_SERVER_PORT": "9090", "APP_LOGGING_LEVEL": "debug"})

        self.assertEqual(cfg.server.port, 9090)
        self.assertEqual(cfg.logging.level, "DEBUG")

    def test_missing_file_raises_config_error(self) -> None:
        with self.assertRaises(ConfigError) as ctx:
            load_config(Path(self.tmp.name) / "missing.toml", env={})
        self.assertIn("missing.toml", str(ctx.exception))

    def test_invalid_port_raises_config_error(self) -> None:
        with self.assertRaises(ConfigError):
            load_config(self.path, env={"APP_SERVER_PORT": "not-a-number"})

    def test_config_is_immutable(self) -> None:
        cfg = load_config(self.path, env={})
        with self.assertRaises(Exception):
            cfg.server.port = 1  # type: ignore[misc]


if __name__ == "__main__":
    unittest.main()
