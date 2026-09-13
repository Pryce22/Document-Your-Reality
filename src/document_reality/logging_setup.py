"""Configure console and daily-file logging for the "dyr" namespace."""
from __future__ import annotations

import logging
import os
import sys
from datetime import date
from pathlib import Path

CONSOLE_FORMAT = "%(asctime)s | %(levelname)-8s | %(name)-12s | %(message)s"
CONSOLE_DATE_FORMAT = "%H:%M:%S"
FILE_FORMAT = "%(asctime)s | %(levelname)-8s | %(name)-12s | %(message)s"
FILE_DATE_FORMAT = "%Y-%m-%d %H:%M:%S"


def setup_logging() -> None:
    """Configure logging once from "DYR_LOG_*" environment variables."""
    console_level_name = os.environ.get("DYR_LOG_LEVEL", "INFO").upper()
    console_level = getattr(logging, console_level_name, logging.INFO)

    root = logging.getLogger("dyr")
    if root.handlers:  # Avoid duplicate handlers after uvicorn reloads.
        return
    root.setLevel(logging.DEBUG)
    root.propagate = False

    console = logging.StreamHandler(sys.stdout)
    console.setLevel(console_level)
    console.setFormatter(logging.Formatter(CONSOLE_FORMAT, datefmt=CONSOLE_DATE_FORMAT))
    root.addHandler(console)

    if os.environ.get("DYR_FILE_LOG", "1") != "0":
        try:
            log_dir = Path(os.environ.get("DYR_LOG_DIR", "logs"))
            log_dir.mkdir(parents=True, exist_ok=True)
            log_file = log_dir / f"backend_{date.today():%Y-%m-%d}.log"
            file_handler = logging.FileHandler(log_file, mode="a", encoding="utf-8")
            file_handler.setLevel(logging.DEBUG)
            file_handler.setFormatter(logging.Formatter(FILE_FORMAT, datefmt=FILE_DATE_FORMAT))
            root.addHandler(file_handler)
            root.info("-" * 70)
            root.info("New backend session -> full DEBUG log in %s", log_file.resolve())
        except OSError as exc:
            root.warning("File logging disabled (cannot write log file: %s)", exc)


def get_logger(name: str) -> logging.Logger:
    """Return a child logger in the application namespace.

    Args:
        name: Child logger name.

    Returns:
        Logger named "dyr.<name>".
    """
    return logging.getLogger(f"dyr.{name}")
