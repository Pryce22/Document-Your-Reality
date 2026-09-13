from __future__ import annotations

from pathlib import Path

from jinja2 import Environment, FileSystemLoader

from document_reality.schemas import Entity, StoredAnnotation

_PROMPTS_DIR = Path(__file__).resolve().parent.parent.parent / "prompts"

_env = Environment(
    loader=FileSystemLoader(str(_PROMPTS_DIR)),
    keep_trailing_newline=False,
    trim_blocks=True,
    lstrip_blocks=True,
)


class KnownObjectCue(dict):
    """Represent one known object in the vision-model prompt."""

    def __init__(self, id: str, name: str, distinctive: str = "", memory: str = ""):
        """Initialize a prompt cue.

        Args:
            id: Persistent entity identifier.
            name: Display name.
            distinctive: Visual re-identification hint.
            memory: Compact known context.
        """
        super().__init__(id=id, name=name, distinctive=distinctive, memory=memory)

    def __getattr__(self, item: str) -> str:
        """Expose dictionary values to Jinja attribute lookup."""
        try:
            return self[item]
        except KeyError as exc:  # pragma: no cover - template typo guard
            raise AttributeError(item) from exc


def render_scan_prompt(
    known: list[KnownObjectCue] | None = None,
    max_objects: int = 8,
) -> str:
    """Render the frame-analysis prompt.

    Args:
        known: Objects available for re-identification.
        max_objects: Maximum objects the model may report.

    Returns:
        Rendered scan prompt.
    """
    return _env.get_template("scan.j2").render(
        known=known or [], max_objects=max_objects
    ).strip()


def render_memory_prompt(
    entity: Entity,
    annotation: StoredAnnotation,
    max_facts: int,
    max_open_points: int,
) -> str:
    """Render the memory-distillation prompt.

    Args:
        entity: Object whose memory is updated.
        annotation: New observation to merge.
        max_facts: Maximum retained facts.
        max_open_points: Maximum retained open points.

    Returns:
        Rendered memory prompt.
    """
    return (
        _env.get_template("memory.j2")
        .render(
            entity_name=entity.display_name(),
            entity_distinctive=entity.distinctive,
            old_summary=entity.memory.summary,
            old_facts=entity.memory.facts,
            old_open_points=entity.memory.open_points,
            ann_title=annotation.annotation.title,
            ann_body=annotation.annotation.body,
            ann_steps=annotation.annotation.steps,
            max_facts=max_facts,
            max_open_points=max_open_points,
        )
        .strip()
    )
