from __future__ import annotations

import time
from collections.abc import AsyncIterator
from datetime import timezone

from document_reality.config import AppConfig
from document_reality.context import ContextBuilder
from document_reality.llm_client import VisionLlmClient
from document_reality.logging_setup import get_logger
from document_reality.prompts import KnownObjectCue
from document_reality.schemas import (
    AnnotationDraft,
    Entity,
    ObjectBox,
    ObjectLabel,
    ScanDetection,
    ScanResponse,
    utc_now,
)
from document_reality.storage import MemoryStore, realm_of

log = get_logger("scan")

# Box overlap provides a fallback when visual re-identification fails.
_SAME_OBJECT_IOU = 0.55


def _normalize_name(name: str) -> str:
    """Normalize an object name for identity comparisons."""
    return " ".join(name.strip().lower().split())


def _nothing_identifiable(started: float) -> ScanResponse:
    """The answer for a frame the model could make nothing out of."""
    return ScanResponse(
        ok=True,
        elapsed_s=round(time.perf_counter() - started, 2),
        labels=[],
        reason="No object clearly identifiable in this frame",
    )


class SceneScanner:
    """Resolve model detections into labels for persistent entities."""

    def __init__(self, config: AppConfig, store: MemoryStore, llm: VisionLlmClient):
        """Initialize the scanner and its persistence context."""
        self.config = config
        self.cfg = config.scan
        self.store = store
        self.llm = llm
        self.context_builder = ContextBuilder(config, store)


    async def scan(
        self,
        image_bytes: bytes,
        source: str = "",
        room_id: str = "",
    ) -> ScanResponse:
        """Analyze a frame and return resolved labels."""
        started = time.perf_counter()

        result = await self.llm.scan_frame(
            image_bytes,
            known=self._known_cues(source),
            max_objects=self.cfg.max_objects_per_frame,
        )
        if not result.objects:
            return _nothing_identifiable(started)

        labels: list[ObjectLabel] = []
        claimed: set[str] = set()
        for detection in result.objects:
            label = self._resolve_one(detection, image_bytes, source, room_id, claimed)
            if label is not None:
                labels.append(label)
                claimed.add(label.entity_id)

        elapsed = time.perf_counter() - started
        log.info(
            "SCAN done in %.1fs -> %d label(s): %s",
            elapsed,
            len(labels),
            ", ".join(
                f"{lb.name}[{lb.entity_id}{'+new' if lb.is_new else ''}"
                f"{'+doc' if lb.updated else ''}]"
                for lb in labels
            )
            or "(nothing recognised)",
        )
        return ScanResponse(
            ok=True,
            elapsed_s=round(elapsed, 2),
            labels=labels,
            reason="" if labels else "No object clearly identifiable in this frame",
        )

    async def scan_stream(
        self,
        image_bytes: bytes,
        source: str = "",
        room_id: str = "",
    ) -> AsyncIterator[ObjectLabel]:
        """Analyze a frame and stream resolved labels.

        Args:
            image_bytes: Encoded source image.
            source: Frame-source identifier.
            room_id: Optional room identifier.

        Yields:
            Each resolved label as soon as it is available.
        """
        started = time.perf_counter()

        claimed: set[str] = set()
        shown: list[str] = []

        async for detection in self.llm.scan_frame_stream(
            image_bytes,
            known=self._known_cues(source),
            max_objects=self.cfg.max_objects_per_frame,
        ):
            label = self._resolve_one(detection, image_bytes, source, room_id, claimed)
            if label is None:
                continue
            claimed.add(label.entity_id)
            shown.append(
                f"{label.name}[{label.entity_id}{'+new' if label.is_new else ''}"
                f"{'+doc' if label.updated else ''}]"
            )
            yield label

        log.info(
            "SCAN(stream) done in %.1fs -> %d label(s): %s",
            time.perf_counter() - started,
            len(shown),
            ", ".join(shown) or "(nothing recognised)",
        )

    def _entity_for_known_id(self, known_id: str) -> Entity | None:
        """Resolve the id the model answered with, however it spelled it.

        Compact models drop the `obj_` prefix, and sometimes a character with it, so a
        re-identification is not lost over a formatting difference.
        """
        wanted = known_id.strip()
        if not wanted:
            return None

        stem = wanted[4:] if wanted.startswith("obj_") else wanted
        for candidate in (wanted, f"obj_{stem}", stem):
            try:
                return self.store.get_entity(candidate)
            except KeyError:
                continue

        # Truncated: accept it only when exactly one object can be meant.
        if len(stem) >= 4:
            matches = [
                entity for entity in self.store.list_entities()
                if entity.entity_id[4:].startswith(stem) or stem.startswith(entity.entity_id[4:])
            ]
            if len(matches) == 1:
                log.info("known_id '%s' matched %s on its stem", known_id, matches[0].entity_id)
                return matches[0]
        return None

    def _resolve_one(
        self,
        detection: ScanDetection,
        image_bytes: bytes,
        source: str,
        room_id: str,
        claimed: set[str],
    ) -> ObjectLabel | None:
        """Validate and resolve one detection into a label."""
        box = detection.box()
        name = detection.name.strip()
        if not box.is_valid():
            log.debug("Dropping detection '%s': degenerate box", name or detection.known_id)
            return None
        # Known detections may omit names; restore them from the store.
        if not name and not detection.known_id.strip():
            log.debug("Dropping detection: neither a name nor a known_id to identify it by")
            return None
        entity, is_new = self._resolve_entity(detection, box, name, source, room_id, claimed)
        if entity is None:
            return None  # already claimed by an earlier detection in this frame

        self.store.touch_entity(
            entity.entity_id, box=box, name=name, distinctive=detection.distinctive
        )

        # Prefer the stored name after identity resolution.
        name = name or entity.display_name()

        updated = False
        if self._should_document(entity, is_new, detection):
            draft = AnnotationDraft(
                object_name=name,
                title=detection.title or name,
                body=detection.body,
                steps=detection.steps,
                tags=detection.tags,
            )
            stored = self.store.create_annotation(entity, draft, image_bytes=image_bytes, box=box)
            updated = True
            log.info(
                "Documented %s '%s' -> annotation %s%s",
                entity.entity_id, entity.display_name(), stored.annotation_id,
                " (first observation)" if is_new else "",
            )

        entity = self.store.get_entity(entity.entity_id)

        # A label reads what was STORED for that object: the annotation this frame wrote if it
        # documented it, the one before if it only recognised it. A label comes BACK unchanged,
        # so only a state change worth storing may alter its text - `_should_document` above.
        stored = self.store.latest_annotation_for_entity(entity.entity_id)
        annotation = stored.annotation if stored is not None else None

        return ObjectLabel(
            entity_id=entity.entity_id,
            name=entity.display_name(),
            title=(annotation.title if annotation else "") or entity.display_name(),
            body=(annotation.body if annotation else "") or entity.memory.summary,
            steps=annotation.steps if annotation else [],
            tags=annotation.tags if annotation else entity.tags,
            box=box,
            is_new=is_new,
            updated=updated,
            annotation_count=entity.annotation_count,
            memory_summary=entity.memory.summary,
            anchor_uuid=entity.anchor_uuid,
            anchor_kind=entity.anchor_kind,
        )

    def _resolve_entity(
        self,
        detection: ScanDetection,
        box: ObjectBox,
        name: str,
        source: str,
        room_id: str,
        claimed: set[str],
    ) -> tuple[Entity | None, bool]:
        """Resolve a detection onto an entity. Returns (entity, is_first_time_ever)."""
        # Level 1: the model says it is an object it already knows.
        if detection.known_id:
            entity = self._entity_for_known_id(detection.known_id)
            if entity is None:
                log.warning(
                    "Model returned known_id '%s' which does not exist -> treating '%s' as new",
                    detection.known_id, name,
                )
                if not name:
                    # An unknown identifier without a name cannot create an entity.
                    log.warning("...and it carried no name either -> detection dropped")
                    return None, False
            else:
                if entity.entity_id in claimed:
                    log.debug("Two detections claim %s in one frame -> keeping the first", entity.entity_id)
                    return None, False
                log.info("RE-IDENTIFIED %s as %s '%s'", name, entity.entity_id, entity.display_name())
                return entity, False

        # Level 2: same name, sitting where a known object was last seen.
        tracked = self._match_by_overlap(name, box, claimed, source)
        if tracked is not None:
            log.info(
                "Tracked '%s' onto %s '%s' by box overlap (model did not re-identify it)",
                name, tracked.entity_id, tracked.display_name(),
            )
            return tracked, False

        # Level 3: same name, same session, seen moments ago.
        recent = self._match_by_recent_name(name, source, claimed)
        if recent is not None:
            log.info(
                "Merged '%s' into %s '%s': same name seen in this session moments ago",
                name, recent.entity_id, recent.display_name(),
            )
            return recent, False

        entity = self.store.create_entity(
            name=name,
            distinctive=detection.distinctive,
            source=source,
            room_id=room_id,
            box=box,
        )
        return entity, True

    def _match_by_overlap(
        self, name: str, box: ObjectBox, claimed: set[str], source: str = ""
    ) -> Entity | None:
        """Find the best unclaimed entity with a matching name and box.

        Scoped to the frame's own world like the cue list is: a chair in the virtual room
        sitting where a chair in the flat once sat is not that chair.
        """
        target = _normalize_name(name)
        realm = realm_of(source)
        best: Entity | None = None
        best_iou = _SAME_OBJECT_IOU
        for entity in self.store.list_entities():
            if entity.entity_id in claimed or _normalize_name(entity.name) != target:
                continue
            if realm_of(entity.source) != realm:
                continue
            iou = box.iou(entity.last_box)
            if iou > best_iou:
                best_iou, best = iou, entity
        return best

    def _match_by_recent_name(self, name: str, source: str, claimed: set[str]) -> Entity | None:
        """Match an unclaimed, recently seen entity by normalized name."""
        window = self.cfg.same_name_merge_seconds
        if window <= 0:
            return None
        target = _normalize_name(name)
        now = utc_now()
        # Include entities omitted from the prompt budget.
        for entity in self.store.list_entities():
            if entity.entity_id in claimed or _normalize_name(entity.name) != target:
                continue
            if source and entity.source and entity.source != source:
                continue
            last_seen = entity.last_seen_at
            if last_seen.tzinfo is None:
                last_seen = last_seen.replace(tzinfo=timezone.utc)
            if (now - last_seen).total_seconds() <= window:
                return entity
        return None

    def _should_document(self, entity: Entity, is_new: bool, detection: ScanDetection) -> bool:
        """Return whether a detection requires a new annotation."""
        if is_new:
            return True
        if not detection.is_new_info:
            return False

        body = " ".join(detection.body.casefold().split())
        if not body:
            log.debug("Not documenting %s: new-info flag has no changed-state body", entity.entity_id)
            return False

        latest = self.store.latest_annotation_for_entity(entity.entity_id)
        if latest is None:
            return True
        previous_body = " ".join(latest.annotation.body.casefold().split())
        if body == previous_body:
            log.debug("Not documenting %s: changed-state body matches the latest observation", entity.entity_id)
            return False
        # Word for word is the rare case: what a model actually sends back is the same
        # observation in a different order, "brown sofa with two cushions" for "two cushions
        # on a brown sofa", and calling that a state change costs a stored annotation and a
        # rewritten label for news nobody had. A real change brings a word that was not there
        # before - opened, empty, moved, off - so requiring one is enough to tell them apart.
        if not set(body.split()) - set(previous_body.split()):
            log.debug(
                "Not documenting %s: the changed-state body only reorders the latest observation",
                entity.entity_id,
            )
            return False

        created = latest.created_at
        if created.tzinfo is None:
            created = created.replace(tzinfo=timezone.utc)
        age_s = (utc_now() - created).total_seconds()
        if age_s < self.cfg.min_seconds_between_annotations:
            log.debug(
                "Not re-documenting %s: last observation is %.0fs old (< %.0fs)",
                entity.entity_id, age_s, self.cfg.min_seconds_between_annotations,
            )
            return False
        return True


    def _known_cues(self, source: str = "") -> list[KnownObjectCue]:
        """Build re-identification cues within the prompt budget.

        Args:
            source: The frame's source, so only objects from the same world are offered.
                See storage.realm_of: the virtual room and the real one never mix.
        """
        cues: list[KnownObjectCue] = []
        spent = 0
        candidates = self.store.recently_seen(
            self.cfg.max_known_objects_in_prompt, realm=realm_of(source)
        )
        for entity in candidates:
            cue = KnownObjectCue(
                id=entity.entity_id,
                name=entity.display_name(),
                distinctive=entity.distinctive[:160],
                memory=self.context_builder.memory_cue(entity, self.config.memory.cue_chars),
            )
            cost = len(cue["id"]) + len(cue["name"]) + len(cue["distinctive"]) + len(cue["memory"]) + 10
            if cues and spent + cost > self.cfg.known_objects_char_budget:
                log.info(
                    "Known-object cues truncated at %d of %d objects (%d chars): the ones left out "
                    "cannot be re-identified this frame. Raise scan.known_objects_char_budget if "
                    "this room is bigger than the budget.",
                    len(cues), len(candidates), spent,
                )
                break
            cues.append(cue)
            spent += cost
        return cues


    def known_labels(self, source: str = "") -> list[ObjectLabel]:
        """Return persisted entities as labels for session restoration.

        Args:
            source: The session's frame source, so a run only restores the labels of its own
                world. Empty = everything, which is what a tool inspecting the store wants.
        """
        realm = realm_of(source) if source else ""
        labels: list[ObjectLabel] = []
        for entity in self.store.list_entities():
            if realm and realm_of(entity.source) != realm:
                continue
            latest = self.store.latest_annotation_for_entity(entity.entity_id)
            draft = latest.annotation if latest else None
            labels.append(
                ObjectLabel(
                    entity_id=entity.entity_id,
                    name=entity.display_name(),
                    title=draft.title if draft else entity.display_name(),
                    body=draft.body if draft else "",
                    steps=draft.steps if draft else [],
                    tags=entity.tags,
                    box=entity.last_box,
                    annotation_count=entity.annotation_count,
                    memory_summary=entity.memory.summary,
                    anchor_uuid=entity.anchor_uuid,
                    anchor_kind=entity.anchor_kind,
                )
            )
        return labels
