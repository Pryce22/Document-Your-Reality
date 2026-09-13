from __future__ import annotations

from document_reality.config import AppConfig
from document_reality.schemas import (
    Entity,
    EntityContext,
    RelatedEntityDigest,
    StoredAnnotation,
)
from document_reality.storage import MemoryStore


def _truncate(text: str, limit: int) -> str:
    """Collapse whitespace and truncate text to "limit" characters."""
    text = " ".join(text.split())
    if len(text) <= limit:
        return text
    return text[: max(0, limit - 1)].rstrip() + "…"


class ContextBuilder:
    """Build compact context for model prompts and Unity overlays."""

    def __init__(self, config: AppConfig, store: MemoryStore):
        """Initialize the builder.

        Args:
            config: Application configuration.
            store: Entity and annotation store.
        """
        self.cfg = config.memory
        self.store = store

    def memory_cue(self, entity: Entity, budget: int) -> str:
        """Build a one-line recognition cue.

        Args:
            entity: Object whose memory is summarized.
            budget: Maximum output characters.

        Returns:
            Priority-ordered summary, facts, and open points.
        """
        memory = entity.memory
        parts: list[str] = []
        if memory.summary:
            parts.append(_truncate(memory.summary, budget))
        else:
            latest = self.store.latest_annotation_for_entity(entity.entity_id)
            if latest is not None and latest.annotation.body:
                parts.append(_truncate(latest.annotation.body, budget))

        used = len(parts[0]) if parts else 0
        for fact in memory.facts[: self.cfg.max_facts]:
            fact = _truncate(fact, 90)
            if used + len(fact) + 3 > budget:
                break
            parts.append(fact)
            used += len(fact) + 3
        for point in memory.open_points[: self.cfg.max_open_points]:
            point = _truncate(point, 90)
            if used + len(point) + 10 > budget:
                break
            parts.append("open: " + point)
            used += len(point) + 10
        return " · ".join(parts)

    def build(self, entity: Entity) -> EntityContext:
        """Build prompt-ready context for an entity.

        Args:
            entity: Object to describe.

        Returns:
            Context with recent annotations and related objects.
        """
        annotations = self.store.annotations_for_entity(entity.entity_id)
        recent = annotations[: self.cfg.max_recent_annotations]
        related = self._related_entities(entity)
        prompt_context = self._render_prompt_context(entity, recent, related)
        return EntityContext(
            entity=entity,
            annotations=recent,
            related=related,
            prompt_context=prompt_context,
        )

    def _related_entities(self, entity: Entity) -> list[RelatedEntityDigest]:
        """Rank related entities by shared tags and room."""
        tags = set(entity.tags)
        scored: list[tuple[float, RelatedEntityDigest]] = []
        for other in self.store.list_entities():
            if other.entity_id == entity.entity_id:
                continue
            shared = sorted(tags & set(other.tags))
            score = 2.0 * len(shared)
            if entity.room_id and other.room_id == entity.room_id:
                score += 1.0
            if score <= 0 or other.annotation_count == 0:
                continue
            summary = other.memory.summary
            if not summary:
                latest = self.store.latest_annotation_for_entity(other.entity_id)
                summary = latest.annotation.body if latest else ""
            scored.append(
                (
                    score,
                    RelatedEntityDigest(
                        entity_id=other.entity_id,
                        name=other.display_name(),
                        summary_line=_truncate(summary, self.cfg.annotation_digest_chars),
                        shared_tags=shared,
                    ),
                )
            )
        scored.sort(key=lambda item: item[0], reverse=True)
        return [digest for _, digest in scored[: self.cfg.max_related_entities]]

    def _render_prompt_context(
        self,
        entity: Entity,
        recent: list[StoredAnnotation],
        related: list[RelatedEntityDigest],
    ) -> str:
        """Render priority-ordered sections within the context budget."""
        budget = self.cfg.context_char_budget
        sections: list[str] = []

        header = f"OBJECT: {entity.display_name()}"
        if entity.distinctive:
            header += f" — {_truncate(entity.distinctive, 160)}"
        header += f" (observations so far: {entity.annotation_count})"
        if entity.qr_payload:
            header += f"\nQR PAYLOAD: {_truncate(entity.qr_payload, 120)}"
        sections.append(header)

        memory = entity.memory
        if memory.summary:
            sections.append("KNOWN SUMMARY: " + _truncate(memory.summary, 400))
        if memory.facts:
            facts = memory.facts[: self.cfg.max_facts]
            sections.append("KNOWN FACTS:\n" + "\n".join(f"- {_truncate(f, 100)}" for f in facts))
        if memory.open_points:
            points = memory.open_points[: self.cfg.max_open_points]
            sections.append("OPEN POINTS:\n" + "\n".join(f"- {_truncate(p, 100)}" for p in points))

        if recent:
            lines = []
            for ann in recent:
                date = ann.created_at.strftime("%Y-%m-%d %H:%M")
                digest = _truncate(f"{ann.annotation.title}: {ann.annotation.body}", self.cfg.annotation_digest_chars)
                lines.append(f"- [{date}] {digest}")
            sections.append("RECENT ANNOTATIONS (newest first):\n" + "\n".join(lines))

        if related:
            lines = []
            for digest in related:
                line = f"- {digest.name}"
                if digest.summary_line:
                    line += f": {digest.summary_line}"
                if digest.shared_tags:
                    line += f" [shared tags: {', '.join(digest.shared_tags)}]"
                lines.append(line)
            sections.append("RELATED OBJECTS NEARBY/LINKED:\n" + "\n".join(lines))

        # Priority order drops the least useful sections first.
        out: list[str] = []
        used = 0
        for section in sections:
            cost = len(section) + 2
            if out and used + cost > budget:
                break
            out.append(section)
            used += cost
        return "\n\n".join(out)
