"""configkit: TOML configuration loading with environment overrides."""

import tomllib
from dataclasses import dataclass, fields
from pathlib import Path
from typing import Any, Mapping


class ConfigError(Exception):
    """Raised when a configuration file or override is invalid."""


@dataclass(frozen=True)
class ServerConfig:
    host: str = "127.0.0.1"
    port: int = 8080


@dataclass(frozen=True)
class LoggingConfig:
    level: str = "INFO"


@dataclass(frozen=True)
class Config:
    server: ServerConfig
    logging: LoggingConfig


_SECTIONS = {"server": ServerConfig, "logging": LoggingConfig}


def _coerce(name: str, current: Any, raw: str) -> Any:
    if isinstance(current, bool):
        return raw.lower() in {"1", "true", "yes"}
    if isinstance(current, int):
        try:
            return int(raw)
        except ValueError as exc:
            raise ConfigError(f"{name}: expected an integer, got {raw!r}") from exc
    if name.lower().endswith("level"):
        return raw.upper()
    return raw


def load_config(path: Path | str, env: Mapping[str, str] | None = None) -> Config:
    """Load ``path`` as TOML and apply ``APP_<SECTION>_<KEY>`` overrides from ``env``."""
    path = Path(path)
    try:
        data = tomllib.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ConfigError(f"configuration file not found: {path}") from exc
    except tomllib.TOMLDecodeError as exc:
        raise ConfigError(f"invalid TOML in {path}: {exc}") from exc
    sections: dict[str, Any] = {}
    for section, cls in _SECTIONS.items():
        values = dict(data.get(section, {}))
        defaults = cls()
        for field in fields(cls):
            key = f"APP_{section.upper()}_{field.name.upper()}"
            if env is not None and key in env:
                values[field.name] = _coerce(key, getattr(defaults, field.name), env[key])
            elif field.name in values and isinstance(getattr(defaults, field.name), int) and not isinstance(values[field.name], bool):
                if not isinstance(values[field.name], int):
                    raise ConfigError(f"{section}.{field.name}: expected an integer")
        unknown = set(values) - {f.name for f in fields(cls)}
        if unknown:
            raise ConfigError(f"unknown keys in [{section}]: {sorted(unknown)}")
        sections[section] = cls(**values)
    return Config(**sections)
