from __future__ import annotations

import threading
from io import BytesIO
from pathlib import Path

from PIL import Image, UnidentifiedImageError

from document_reality.config import AppConfig
from document_reality.logging_setup import get_logger
from document_reality.schemas import (
    AnnotationDraft,
    AnnotationPatch,
    Entity,
    EntityMemory,
    ObjectBox,
    StoredAnnotation,
    utc_now,
)

log = get_logger("store")


def realm_of(source: str) -> str:
    """Which world a frame came from: the virtual room, or the real one.

    Args:
        source: Frame-source identifier, e.g. "video:room.mp4", "vr-room", "headset".

    Returns:
        "vr" for the virtual room, "real" for anything else.
    """
    return "vr" if source.strip().lower().startswith("vr") else "real"


class StorageError(RuntimeError):
    """Report invalid or unwritable persistent data."""
    pass


class MemoryStore:
    """Persist entities, annotations, and captures with in-memory indexes."""

    def __init__(self, config: AppConfig):
        """Initialize indexes and load persisted records."""
        self.config = config
        self.entities_dir = config.storage.entities_dir
        self.annotations_dir = config.storage.annotations_dir
        self.captures_dir = config.storage.captures_dir
        self._lock = threading.RLock()
        self._entities: dict[str, Entity] = {}
        self._by_anchor: dict[str, str] = {}
        self._by_qr: dict[str, str] = {}
        self._annotations: dict[str, StoredAnnotation] = {}
        self._by_entity: dict[str, list[str]] = {}
        self._load()


    def _load(self) -> None:
        """Load valid entities and annotations from disk."""
        for path in sorted(self.entities_dir.glob("obj_*.json")):
            try:
                entity = Entity.model_validate_json(path.read_text(encoding="utf-8"))
            except Exception:
                log.warning("Skipping unreadable entity file %s", path.name)
                continue
            self._entities[entity.entity_id] = entity
            self._index(entity)
        for path in sorted(self.annotations_dir.glob("ann_*.json")):
            try:
                ann = StoredAnnotation.model_validate_json(path.read_text(encoding="utf-8"))
            except Exception:
                log.warning("Skipping unreadable annotation file %s", path.name)
                continue
            self._annotations[ann.annotation_id] = ann
            self._by_entity.setdefault(ann.entity_id, []).append(ann.annotation_id)

    def _index(self, entity: Entity) -> None:
        """Index an entity's anchor identifiers."""
        if entity.anchor_uuid:
            self._by_anchor[entity.anchor_uuid] = entity.entity_id
        if entity.qr_payload:
            self._by_qr[entity.qr_payload] = entity.entity_id


    def create_entity(
        self,
        name: str,
        distinctive: str = "",
        source: str = "",
        room_id: str = "",
        box: ObjectBox | None = None,
    ) -> Entity:
        """Create and persist a new entity."""
        with self._lock:
            entity = Entity(
                name=name.strip(),
                distinctive=distinctive.strip(),
                source=source,
                room_id=room_id,
                last_box=box or ObjectBox(),
            )
            self._entities[entity.entity_id] = entity
            self._write_entity(entity)
            log.info("NEW object %s '%s' (%s)", entity.entity_id, entity.name, entity.distinctive[:60])
            return entity

    def get_entity(self, entity_id: str) -> Entity:
        """Return an entity by identifier."""
        with self._lock:
            if entity_id not in self._entities:
                raise KeyError(entity_id)
            return self._entities[entity_id]

    def find_by_anchor(self, anchor_uuid: str) -> Entity | None:
        """Resolve an entity from a spatial or QR anchor.

        Args:
            anchor_uuid: Anchor UUID or QR payload.

        Returns:
            Matching entity, if present.
        """
        with self._lock:
            key = anchor_uuid.strip().lower()
            entity_id = self._by_anchor.get(key) or self._by_qr.get(anchor_uuid.strip())
            return self._entities.get(entity_id) if entity_id else None

    def bind_anchor(
        self,
        entity_id: str,
        anchor_uuid: str,
        anchor_kind: str = "spatial",
        qr_payload: str = "",
        room_id: str = "",
    ) -> Entity:
        """Attach a headset anchor to an object so its label survives sessions."""
        with self._lock:
            entity = self.get_entity(entity_id)
            entity.anchor_uuid = anchor_uuid.strip().lower()
            entity.anchor_kind = anchor_kind  # type: ignore[assignment]
            if qr_payload:
                entity.qr_payload = qr_payload
            if room_id:
                entity.room_id = room_id
            self._index(entity)
            self._write_entity(entity)
            log.info(
                "Object %s '%s' anchored to %s anchor %s",
                entity.entity_id, entity.display_name(), anchor_kind, entity.anchor_uuid,
            )
            return entity

    def touch_entity(
        self,
        entity_id: str,
        box: ObjectBox | None = None,
        name: str = "",
        distinctive: str = "",
    ) -> Entity:
        """Mark an object as seen again, refreshing what helps re-identify it."""
        with self._lock:
            entity = self.get_entity(entity_id)
            entity.last_seen_at = utc_now()
            if box is not None and box.is_valid():
                entity.last_box = box
            if name and not entity.name:
                entity.name = name.strip()
            # Keep the latest visual cue for re-identification.
            if distinctive:
                entity.distinctive = distinctive.strip()
            self._write_entity(entity)
            return entity

    def list_entities(self) -> list[Entity]:
        """Return entities in creation order."""
        with self._lock:
            return sorted(self._entities.values(), key=lambda e: e.created_at)

    def recently_seen(self, limit: int, realm: str = "") -> list[Entity]:
        """Return the most recently seen entities.

        Args:
            limit: Maximum entities to return.
            realm: Only entities from this world (see "realm_of"). Empty = all of them.

        Returns:
            Entities ordered by latest sighting.
        """
        with self._lock:
            candidates = [
                entity for entity in self._entities.values()
                if not realm or realm_of(entity.source) == realm
            ]
        return sorted(
            candidates,
            key=lambda e: (e.last_seen_at, e.created_at),
            reverse=True,
        )[:limit]

    def update_entity_memory(self, entity_id: str, memory: EntityMemory) -> Entity:
        """Replace and version an entity's distilled memory."""
        with self._lock:
            entity = self.get_entity(entity_id)
            memory.version = entity.memory.version + 1
            memory.updated_at = utc_now()
            entity.memory = memory
            self._write_entity(entity)
            return entity

    def delete_entity(self, entity_id: str) -> None:
        """Delete an entity and all associated data."""
        with self._lock:
            entity = self.get_entity(entity_id)
            for annotation_id in list(self._by_entity.get(entity_id, [])):
                self.delete_annotation(annotation_id)
            self._by_anchor.pop(entity.anchor_uuid, None)
            self._by_qr.pop(entity.qr_payload, None)
            self._by_entity.pop(entity_id, None)
            del self._entities[entity_id]
            path = self.entities_dir / f"{entity_id}.json"
            if path.exists():
                path.unlink()

    def reset(self) -> tuple[int, int]:
        """Remove all entities, annotations, and captures.

        Returns:
            Counts of removed entities and annotations.
        """
        with self._lock:
            entities = len(self._entities)
            annotations = len(self._annotations)

            for annotation_id in list(self._annotations):
                for path in (self.annotations_dir / f"{annotation_id}.json",
                             self.captures_dir / f"{annotation_id}.jpg"):
                    if path.exists():
                        path.unlink()
            for entity_id in list(self._entities):
                path = self.entities_dir / f"{entity_id}.json"
                if path.exists():
                    path.unlink()

            self._entities.clear()
            self._annotations.clear()
            self._by_anchor.clear()
            self._by_qr.clear()
            self._by_entity.clear()

        log.info("Store reset -> removed %d object(s) and %d observation(s)",
                 entities, annotations)
        return entities, annotations


    def create_annotation(
        self,
        entity: Entity,
        draft: AnnotationDraft,
        image_bytes: bytes | None = None,
        box: ObjectBox | None = None,
    ) -> StoredAnnotation:
        """Create and persist an entity observation."""
        with self._lock:
            stored = StoredAnnotation(
                entity_id=entity.entity_id,
                room_id=entity.room_id,
                llm_model=self.config.llm.model,
                box=box or entity.last_box,
                annotation=draft,
            )
            if image_bytes:
                stored.image_file = str(self._save_capture(stored.annotation_id, image_bytes))

            self._annotations[stored.annotation_id] = stored
            self._by_entity.setdefault(entity.entity_id, []).insert(0, stored.annotation_id)
            self._write_annotation(stored)

            # Keep the object's aggregate view current.
            entity.annotation_count = len(self._by_entity[entity.entity_id])
            for tag in draft.tags:
                if tag not in entity.tags:
                    entity.tags.append(tag)
            if not entity.name and draft.object_name:
                entity.name = draft.object_name
            entity.last_seen_at = utc_now()
            self._write_entity(entity)
            return stored

    def annotations_for_entity(self, entity_id: str) -> list[StoredAnnotation]:
        """Return an entity's annotations, newest first."""
        with self._lock:
            ids = self._by_entity.get(entity_id, [])
            anns = [self._annotations[i] for i in ids if i in self._annotations]
            return sorted(anns, key=lambda a: a.created_at, reverse=True)

    def latest_annotation_for_entity(self, entity_id: str) -> StoredAnnotation | None:
        """Return an entity's newest annotation, if any."""
        anns = self.annotations_for_entity(entity_id)
        return anns[0] if anns else None

    def list_annotations(self) -> list[StoredAnnotation]:
        """Return all annotations, newest first."""
        with self._lock:
            return sorted(self._annotations.values(), key=lambda a: a.created_at, reverse=True)

    def get_annotation(self, annotation_id: str) -> StoredAnnotation:
        """Return an annotation by identifier."""
        with self._lock:
            if annotation_id not in self._annotations:
                raise KeyError(annotation_id)
            return self._annotations[annotation_id]

    def update_annotation(self, annotation_id: str, patch: AnnotationPatch) -> StoredAnnotation:
        """Apply and persist an annotation patch."""
        with self._lock:
            stored = self.get_annotation(annotation_id)
            if patch.status is not None:
                stored.status = patch.status
            if patch.annotation is not None:
                stored.annotation = patch.annotation
            self._write_annotation(stored)
            return stored

    def delete_annotation(self, annotation_id: str) -> None:
        """Delete an annotation and its capture."""
        with self._lock:
            stored = self.get_annotation(annotation_id)
            del self._annotations[annotation_id]
            ids = self._by_entity.get(stored.entity_id, [])
            if annotation_id in ids:
                ids.remove(annotation_id)
            path = self.annotations_dir / f"{annotation_id}.json"
            if path.exists():
                path.unlink()
            if stored.image_file:
                image_path = Path(stored.image_file)
                if image_path.exists():
                    image_path.unlink()
            if stored.entity_id in self._entities:
                entity = self._entities[stored.entity_id]
                entity.annotation_count = len(ids)
                self._write_entity(entity)


    def _write_entity(self, entity: Entity) -> None:
        """Persist an entity as JSON."""
        path = self.entities_dir / f"{entity.entity_id}.json"
        path.write_text(entity.model_dump_json(indent=2), encoding="utf-8")

    def _write_annotation(self, stored: StoredAnnotation) -> None:
        """Persist an annotation as JSON."""
        path = self.annotations_dir / f"{stored.annotation_id}.json"
        path.write_text(stored.model_dump_json(indent=2), encoding="utf-8")

    def _save_capture(self, annotation_id: str, image_bytes: bytes) -> Path:
        """Validate and persist an uploaded image as JPEG."""
        try:
            image = Image.open(BytesIO(image_bytes)).convert("RGB")
        except UnidentifiedImageError as exc:
            raise StorageError("Uploaded file is not a valid image") from exc
        output_path = self.captures_dir / f"{annotation_id}.jpg"
        image.save(output_path, format="JPEG", quality=92)
        return output_path
