from __future__ import annotations

import logging
import os
from pathlib import Path
from typing import Any

import yaml
from pydantic import BaseModel, Field


class LlmConfig(BaseModel):
    """Configure the OpenAI-compatible vision provider."""

    base_url: str = "http://localhost:11434/v1"
    api_key: str = "none"
    model: str = "gemma4:e2b"
    max_tokens: int = 1024
    # Scans need roughly 120 tokens per documented object.
    scan_max_tokens: int = 2048
    timeout_s: float = 360.0
    temperature: float = 0.2
    # Zero disables image downscaling.
    max_image_px: int = 896
    image_jpeg_quality: int = 80
    # Zero runs only the startup probe.
    keep_warm_interval_s: float = 240.0
    # Prevent reasoning-only responses from exhausting the output budget.
    disable_thinking: bool = False
    # Constrain the scan reply to the ScanResult schema. Needs disable_thinking.
    scan_constrained: bool = False


class StorageConfig(BaseModel):
    """Configure persistent data directories."""

    entities_dir: Path = Path("./data/entities")
    annotations_dir: Path = Path("./data/annotations")
    captures_dir: Path = Path("./data/captures")


class MemoryConfig(BaseModel):
    """Configure prompt context and distilled-memory limits."""

    context_char_budget: int = 1800
    # Per-object memory budget in the scan prompt.
    cue_chars: int = 220
    max_recent_annotations: int = 5
    max_related_entities: int = 3
    max_facts: int = 8
    max_open_points: int = 4
    annotation_digest_chars: int = 140


class ScanConfig(BaseModel):
    """Configure detection, re-identification, and annotation policies."""

    # More objects produce longer model responses.
    max_objects_per_frame: int = 5
    # Character budgets degrade more predictably than fixed object counts.
    known_objects_char_budget: int = 4000
    # Hard ceiling for stores containing hundreds of objects.
    max_known_objects_in_prompt: int = 40
    # Avoid repeated annotations from a short scan loop.
    min_seconds_between_annotations: float = 25.0
    # Zero disables the same-session duplicate guard.
    same_name_merge_seconds: float = 120.0


class AppConfig(BaseModel):
    """Aggregate application configuration."""

    llm: LlmConfig = Field(default_factory=LlmConfig)
    storage: StorageConfig = Field(default_factory=StorageConfig)
    memory: MemoryConfig = Field(default_factory=MemoryConfig)
    scan: ScanConfig = Field(default_factory=ScanConfig)

    def ensure_dirs(self) -> None:
        """Create configured storage directories when missing."""
        self.storage.entities_dir.mkdir(parents=True, exist_ok=True)
        self.storage.annotations_dir.mkdir(parents=True, exist_ok=True)
        self.storage.captures_dir.mkdir(parents=True, exist_ok=True)


# Precedence: process environment, adjacent .env file, then YAML.

_ENV_OVERRIDES = {
    "DYR_LLM_BASE_URL": ("llm", "base_url"),
    "DYR_LLM_MODEL": ("llm", "model"),
    "DYR_LLM_API_KEY": ("llm", "api_key"),
    "DYR_LLM_DISABLE_THINKING": ("llm", "disable_thinking"),
}


def _read_dotenv(path: Path) -> dict[str, str]:
    """Read "KEY=VALUE" pairs from a dotenv file.

    Args:
        path: Dotenv file to read.

    Returns:
        Parsed values, or an empty mapping when absent.
    """
    values: dict[str, str] = {}
    if not path.exists():
        return values
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        values[key.strip()] = value.strip().strip("'\"")
    return values


def _apply_env_overrides(data: dict[str, Any], config_path: Path) -> dict[str, Any]:
    """Apply supported environment overrides.

    Args:
        data: Mutable configuration mapping.
        config_path: YAML path used to locate the dotenv file.

    Returns:
        Updated configuration mapping.
    """
    dotenv = _read_dotenv(config_path.parent / ".env")
    log = logging.getLogger("dyr.config")
    for env_key, (section, field) in _ENV_OVERRIDES.items():
        value = os.environ.get(env_key) or dotenv.get(env_key)
        if value:
            data.setdefault(section, {})[field] = value
            shown = value if field != "api_key" else "***"
            log.info("Config override %s -> %s.%s = %s", env_key, section, field, shown)
    return data


def _make_paths_relative_to_config(data: dict[str, Any], config_path: Path) -> dict[str, Any]:
    """Resolve storage paths against the YAML directory.

    Args:
        data: Mutable configuration mapping.
        config_path: Source YAML path.

    Returns:
        Updated configuration mapping.
    """
    base_dir = config_path.parent.resolve()
    path_fields = [
        ("storage", "entities_dir"),
        ("storage", "annotations_dir"),
        ("storage", "captures_dir"),
    ]
    for section, key in path_fields:
        value = data.get(section, {}).get(key)
        if not value:
            continue
        path = Path(value)
        if not path.is_absolute():
            data[section][key] = str(base_dir / path)
    return data


def load_config(path: str | Path = "config.yaml") -> AppConfig:
    """Load and initialize application configuration.

    Args:
        path: YAML configuration path.

    Returns:
        Validated configuration with initialized storage directories.

    Raises:
        FileNotFoundError: If "path" does not exist.
    """
    config_path = Path(path).expanduser().resolve()
    if not config_path.exists():
        raise FileNotFoundError(f"Config file not found: {config_path}")
    with config_path.open("r", encoding="utf-8") as f:
        raw = yaml.safe_load(f) or {}
    raw = _apply_env_overrides(raw, config_path)
    raw = _make_paths_relative_to_config(raw, config_path)
    config = AppConfig.model_validate(raw)
    config.ensure_dirs()
    return config
