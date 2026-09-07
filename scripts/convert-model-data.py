#!/usr/bin/env python
"""Convert inspect_ai's model metadata YAML files into the JSON resources embedded in InspectAzureAI.Eval.

Source: ``src/inspect_ai/model/_model_data/*.yml`` in the inspect_ai checkout. Each YAML file becomes
one JSON file of the same base name with the same structure (organization -> display_name + models ->
fields, versions, aliases); dates become ISO ``YYYY-MM-DD`` strings, exactly what Python's ``UtcDate``
validator accepts. A ``manifest.json`` records the source commit and the file order.

The file order matters: Python's ``read_model_info`` walks ``Path.glob("*.yml")`` (filesystem order)
and later entries win in the case-insensitive lookup index. ``FILE_ORDER`` pins the order observed on
the reference machine so the .NET lookup resolves the same winners as the Python reference values in
the tests. Exact keys never collide across files (checked here), so only that index depends on order.

Run with the inspect_ai virtualenv (it has pyyaml):

    /path/to/inspect_ai/.venv/bin/python scripts/convert-model-data.py \
        --source /path/to/inspect_ai/src/inspect_ai/model/_model_data \
        --out src/InspectAzureAI.Eval/Model/Cost/ModelData
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import subprocess
import sys
from pathlib import Path

import yaml

FILE_ORDER = [
    "anthropic.yml",
    "grok.yml",
    "gdm.yml",
    "moonshotai.yml",
    "together.yml",
    "mistral.yml",
    "deepseek.yml",
    "fireworks.yml",
    "openai.yml",
]

# BaseModelDefinition fields (model_data.py); "versions" is allowed on a model definition only.
STR_FIELDS = ("display_name", "reasoning_effort_default", "family", "snapshot")
DATE_FIELDS = ("release_date", "knowledge_cutoff_date")
INT_FIELDS = ("context_length", "output_tokens", "input_tokens")
BOOL_FIELDS = ("reasoning",)
KNOWN_FIELDS = set(STR_FIELDS + DATE_FIELDS + INT_FIELDS + BOOL_FIELDS + ("aliases", "versions"))


class ConversionError(Exception):
    pass


def _date(value: object, where: str) -> str:
    # mirrors _before_validate_utc_date: str -> date.fromisoformat, datetime -> .date(), date as is
    if isinstance(value, dt.datetime):
        return value.date().isoformat()
    if isinstance(value, dt.date):
        return value.isoformat()
    if isinstance(value, str):
        return dt.date.fromisoformat(value.replace("Z", "+00:00")).isoformat()
    raise ConversionError(f"{where}: expected a date, got {type(value).__name__} {value!r}")


def _definition(raw: object, where: str, allow_versions: bool, warnings: list[str]) -> dict[str, object]:
    if raw is None:
        return {}
    if not isinstance(raw, dict):
        raise ConversionError(f"{where}: expected a mapping, got {type(raw).__name__}")
    out: dict[str, object] = {}
    for key, value in raw.items():
        if key not in KNOWN_FIELDS or (key == "versions" and not allow_versions):
            # pydantic's default extra="ignore" drops these silently; say so instead
            warnings.append(f"{where}: ignoring unknown field {key!r}")
            continue
        if value is None:
            continue
        if key in STR_FIELDS:
            if not isinstance(value, str):
                raise ConversionError(f"{where}.{key}: expected str, got {type(value).__name__} {value!r}")
            out[key] = value
        elif key in DATE_FIELDS:
            out[key] = _date(value, f"{where}.{key}")
        elif key in INT_FIELDS:
            if isinstance(value, bool) or not isinstance(value, int):
                raise ConversionError(f"{where}.{key}: expected int, got {type(value).__name__} {value!r}")
            out[key] = value
        elif key in BOOL_FIELDS:
            if not isinstance(value, bool):
                raise ConversionError(f"{where}.{key}: expected bool, got {type(value).__name__} {value!r}")
            out[key] = value
        elif key == "aliases":
            if not isinstance(value, list) or not all(isinstance(a, str) for a in value):
                raise ConversionError(f"{where}.aliases: expected a list of str")
            out[key] = list(value)
        elif key == "versions":
            if not isinstance(value, dict):
                raise ConversionError(f"{where}.versions: expected a mapping")
            out[key] = {
                _key(name, f"{where}.versions"): _definition(vdef, f"{where}.versions.{name}", False, warnings)
                for name, vdef in value.items()
            }
    return out


def _key(name: object, where: str) -> str:
    if not isinstance(name, str):
        raise ConversionError(f"{where}: model names must be strings, got {type(name).__name__} {name!r}")
    return name


def convert_file(path: Path, warnings: list[str]) -> dict[str, object]:
    with path.open("r", encoding="utf-8") as f:
        data = yaml.safe_load(f)
    if not data:
        return {}
    if not isinstance(data, dict):
        raise ConversionError(f"{path.name}: top level must be a mapping of organizations")
    result: dict[str, object] = {}
    for org, org_data in data.items():
        where = f"{path.name}:{org}"
        if not isinstance(org, str) or not isinstance(org_data, dict):
            raise ConversionError(f"{where}: organization entries must be mappings keyed by name")
        display_name = org_data.get("display_name")
        if not isinstance(display_name, str):
            raise ConversionError(f"{where}: display_name is required")
        models = org_data.get("models")
        if not isinstance(models, dict):
            raise ConversionError(f"{where}: models mapping is required")
        result[org] = {
            "display_name": display_name,
            "models": {
                _key(name, where): _definition(mdef, f"{where}.{name}", True, warnings)
                for name, mdef in models.items()
            },
        }
    return result


def collect_keys(converted: dict[str, object]) -> list[str]:
    keys: list[str] = []
    for org, org_data in converted.items():
        for name, mdef in org_data["models"].items():  # type: ignore[index]
            keys.append(f"{org}/{name}")
            keys += [f"{org}/{a}" for a in mdef.get("aliases", [])]
            for vname, vdef in mdef.get("versions", {}).items():
                keys.append(f"{org}/{vname}")
                keys += [f"{org}/{a}" for a in vdef.get("aliases", [])]
    return keys


def source_commit(source: Path) -> str | None:
    try:
        return subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    except (OSError, subprocess.CalledProcessError):
        return None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", type=Path, required=True, help="inspect_ai's model/_model_data directory")
    parser.add_argument("--out", type=Path, required=True, help="output directory for the JSON resources")
    args = parser.parse_args()

    present = sorted(p.name for p in args.source.glob("*.yml"))
    if present != sorted(FILE_ORDER):
        raise ConversionError(
            f"YAML files changed: found {present}, FILE_ORDER has {sorted(FILE_ORDER)}; update FILE_ORDER"
        )

    args.out.mkdir(parents=True, exist_ok=True)
    warnings: list[str] = []
    seen: dict[str, str] = {}
    files: list[str] = []
    total = 0
    for name in FILE_ORDER:
        converted = convert_file(args.source / name, warnings)
        for key in collect_keys(converted):
            if key in seen and seen[key] != name:
                raise ConversionError(f"key {key!r} appears in both {seen[key]} and {name}")
            seen[key] = name
        json_name = Path(name).with_suffix(".json").name
        with (args.out / json_name).open("w", encoding="utf-8") as f:
            json.dump(converted, f, indent=2, ensure_ascii=True)
            f.write("\n")
        files.append(json_name)
        total += sum(len(o["models"]) for o in converted.values())  # type: ignore[index]

    manifest = {
        "source": {"repo": "inspect_ai", "path": "src/inspect_ai/model/_model_data", "commit": source_commit(args.source)},
        "generated_by": "scripts/convert-model-data.py",
        "files": files,
    }
    with (args.out / "manifest.json").open("w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")

    for w in warnings:
        print(f"warning: {w}", file=sys.stderr)
    print(f"wrote {len(files)} files, {total} model definitions, {len(seen)} lookup keys -> {args.out}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except ConversionError as e:
        print(f"error: {e}", file=sys.stderr)
        sys.exit(1)
