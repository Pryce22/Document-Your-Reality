from __future__ import annotations

from datetime import datetime, timezone
from typing import Literal
from uuid import uuid4

from pydantic import BaseModel, Field, field_validator


def utc_now() -> datetime:
    """Return the current timezone-aware UTC timestamp."""
    return datetime.now(timezone.utc)


MAX_STEPS = 4
MAX_TAGS = 6



# Scene anchors use the raycast target's hierarchy path.
AnchorKind = Literal["", "qr", "spatial", "mruk", "scene"]


class ObjectBox(BaseModel):
    """Represent a normalized image-space bounding box."""

    x0: float = 0.0
    y0: float = 0.0
    x1: float = 0.0
    y1: float = 0.0

    @classmethod
    def from_edges(cls, x0: float, y0: float, x1: float, y1: float) -> "ObjectBox":
        """Convert named 0-1000 edges into a normalized box.

        Args:
            x0: Left edge across the image width.
            y0: Top edge down the image height.
            x1: Right edge.
            y1: Bottom edge.

        Returns:
            Clamped box with ordered corners.
        """
        left, top, right, bottom = (float(v) / 1000.0 for v in (x0, y0, x1, y1))
        if right < left:
            left, right = right, left
        if bottom < top:
            top, bottom = bottom, top
        clamp = lambda v: min(1.0, max(0.0, v))  # noqa: E731
        return cls(x0=clamp(left), y0=clamp(top), x1=clamp(right), y1=clamp(bottom))

    @classmethod
    def from_box_2d(cls, box: list[int] | list[float] | None) -> "ObjectBox":
        """Convert a positional box into a normalized one.

        Args:
            box: "[y0, x0, y1, x1]" coordinates in the 0-1000 range.

        Returns:
            Clamped box with ordered corners.
        """
        if not box or len(box) < 4:
            return cls()
        y0, x0, y1, x1 = box[:4]
        return cls.from_edges(x0, y0, x1, y1)

    @property
    def center(self) -> tuple[float, float]:
        """Return the normalized box center."""
        return (self.x0 + self.x1) / 2.0, (self.y0 + self.y1) / 2.0

    @property
    def area(self) -> float:
        """Return the normalized box area."""
        return max(0.0, self.x1 - self.x0) * max(0.0, self.y1 - self.y0)

    def is_valid(self) -> bool:
        """Return whether the box is large enough to use."""
        return self.area > 0.0004  # ~2% of each side: smaller is noise

    def iou(self, other: "ObjectBox") -> float:
        """Compute intersection over union with another box.

        Args:
            other: Box to compare.

        Returns:
            Intersection-over-union score in the 0-1 range.
        """
        ix0, iy0 = max(self.x0, other.x0), max(self.y0, other.y0)
        ix1, iy1 = min(self.x1, other.x1), min(self.y1, other.y1)
        inter = max(0.0, ix1 - ix0) * max(0.0, iy1 - iy0)
        union = self.area + other.area - inter
        return inter / union if union > 0 else 0.0




class EntityMemory(BaseModel):
    """Store compact model-maintained knowledge about an entity."""

    summary: str = Field(default="", description="Distilled description of everything known about the object.")
    facts: list[str] = Field(default_factory=list, description="Short atomic facts (max ~15 words each).")
    open_points: list[str] = Field(default_factory=list, description="Unresolved questions / pending work.")
    updated_at: datetime = Field(default_factory=utc_now)
    version: int = 0


class Entity(BaseModel):
    """One physical object the system knows about."""

    entity_id: str = Field(default_factory=lambda: f"obj_{uuid4().hex[:8]}")
    name: str = Field(default="", description="Short name of the object, from the vision model.")
    distinctive: str = Field(
        default="",
        description="How to tell THIS instance from another of the same kind. Fed back to the "
        "model as the re-identification cue.",
    )
    # Anchoring: empty until Unity binds a real anchor to the object.
    anchor_uuid: str = Field(default="", description="Meta Spatial Anchor / MRUK trackable UUID.")
    anchor_kind: AnchorKind = ""
    qr_payload: str = ""
    room_id: str = ""
    source: str = Field(default="", description="Where it was first seen, e.g. 'video:room.mp4' or 'headset'.")
    last_box: ObjectBox = Field(default_factory=ObjectBox, description="Last known box, for 2D label tracking.")
    created_at: datetime = Field(default_factory=utc_now)
    last_seen_at: datetime = Field(default_factory=utc_now)
    tags: list[str] = Field(default_factory=list)
    annotation_count: int = 0
    memory: EntityMemory = Field(default_factory=EntityMemory)

    def display_name(self) -> str:
        """Return the best available human-readable identifier."""
        return self.name or self.qr_payload or self.entity_id




class AnnotationDraft(BaseModel):
    """Represent one model-generated observation before persistence."""

    object_name: str = Field(default="", description="Short name of the object (2-4 words).")
    title: str = Field(default="", description="Short AR overlay title.")
    body: str = Field(default="", description="Main annotation text, concise and operational.")
    steps: list[str] = Field(default_factory=list, description="Ordered action steps, if any.")
    tags: list[str] = Field(default_factory=list, description="Lowercase keywords for linking related objects.")

    @field_validator("tags")
    @classmethod
    def normalize_tags(cls, value: list[str]) -> list[str]:
        """Normalize, deduplicate, and cap annotation tags."""
        seen: list[str] = []
        for item in value:
            item = item.strip().lower()
            if item and item not in seen:
                seen.append(item)
        return seen[:MAX_TAGS]

    @field_validator("steps")
    @classmethod
    def clean_steps(cls, value: list[str]) -> list[str]:
        """Remove blank steps and enforce the configured limit."""
        return [s.strip() for s in value if s.strip()][:MAX_STEPS]


class StoredAnnotation(BaseModel):
    """Represent a persisted entity observation."""

    annotation_id: str = Field(default_factory=lambda: f"ann_{uuid4().hex[:12]}")
    entity_id: str
    room_id: str = ""
    status: Literal["draft", "approved", "rejected"] = "draft"
    created_at: datetime = Field(default_factory=utc_now)
    image_file: str = Field(default="", description="Full frame this observation came from.")
    box: ObjectBox = Field(default_factory=ObjectBox, description="Where the object was in that frame.")
    llm_model: str = ""
    annotation: AnnotationDraft


class AnnotationPatch(BaseModel):
    """Define mutable fields of a stored annotation."""

    status: Literal["draft", "approved", "rejected"] | None = None
    annotation: AnnotationDraft | None = None




class RelatedEntityDigest(BaseModel):
    """Summarize an entity related to the current object."""

    entity_id: str
    name: str
    summary_line: str = ""
    shared_tags: list[str] = Field(default_factory=list)


class EntityContext(BaseModel):
    """Bundle entity history for Unity and model prompts."""

    entity: Entity
    annotations: list[StoredAnnotation] = Field(default_factory=list)
    related: list[RelatedEntityDigest] = Field(default_factory=list)
    prompt_context: str = Field(default="", description="Compact text block ready for a small-model prompt.")




class ScanDetection(BaseModel):
    """Represent one unresolved vision-model detection."""

    known_id: str = Field(default="", description="entity_id of an already documented object, or empty.")
    name: str = ""
    distinctive: str = ""
    # Named edges, because an ORDER is something a model gets wrong in silence: Gemma answers
    # [y0, x0, y1, x1] and Qwen answers [x0, y0, x1, y1]. Read with the wrong convention every
    # box lands on a different object and nothing in the reply says so. A named edge cannot be
    # swapped; scripts/check_model.py probes a new model for exactly this.
    x0: int | None = Field(default=None, description="Left edge, 0-1000 across the image width.")
    y0: int | None = Field(default=None, description="Top edge, 0-1000 down the image height.")
    x1: int | None = Field(default=None, description="Right edge, 0-1000.")
    y1: int | None = Field(default=None, description="Bottom edge, 0-1000.")
    box_2d: list[int] = Field(
        default_factory=list,
        description="Positional fallback, read as [y0, x0, y1, x1] integers 0-1000.",
    )
    title: str = ""
    body: str = ""
    steps: list[str] = Field(default_factory=list)
    tags: list[str] = Field(default_factory=list)
    is_new_info: bool = Field(
        default=False,
        description="Whether this detection contains a changed object state.",
    )

    def box(self) -> ObjectBox:
        """Where this detection is, from whichever form the model answered in."""
        if None not in (self.x0, self.y0, self.x1, self.y1):
            return ObjectBox.from_edges(self.x0, self.y0, self.x1, self.y1)
        return ObjectBox.from_box_2d(self.box_2d)

    def copy_box_from(self, other: "ScanDetection") -> None:
        """Take another detection's outline, in the same form it arrived in."""
        self.x0, self.y0, self.x1, self.y1 = other.x0, other.y0, other.x1, other.y1
        self.box_2d = list(other.box_2d)


class ScanResult(BaseModel):
    """Collect detections returned by one scan."""

    objects: list[ScanDetection] = Field(default_factory=list)




class ObjectLabel(BaseModel):
    """Represent a resolved MR label ready for Unity."""

    entity_id: str
    name: str = ""
    title: str = ""
    body: str = ""
    steps: list[str] = Field(default_factory=list)
    tags: list[str] = Field(default_factory=list)
    box: ObjectBox = Field(default_factory=ObjectBox)
    is_new: bool = Field(default=False, description="First time this object is documented.")
    updated: bool = Field(default=False, description="A new annotation was saved for it in this scan.")
    annotation_count: int = 0
    memory_summary: str = ""
    anchor_uuid: str = ""
    anchor_kind: AnchorKind = ""


class ScanResponse(BaseModel):
    """Return the outcome and labels of a frame scan."""

    ok: bool = True
    reason: str = ""
    elapsed_s: float = 0.0
    labels: list[ObjectLabel] = Field(default_factory=list)


class AnchorBindRequest(BaseModel):
    """Bind an entity to an anchor reported by Unity."""

    anchor_uuid: str
    anchor_kind: AnchorKind = "spatial"
    qr_payload: str = ""
    room_id: str = ""

    @field_validator("anchor_uuid")
    @classmethod
    def normalize_uuid(cls, value: str) -> str:
        """Normalize and validate an anchor identifier.

        Raises:
            ValueError: If the identifier is blank.
        """
        value = value.strip().lower()
        if not value:
            raise ValueError("anchor_uuid must not be empty")
        return value


class ObjectListResponse(BaseModel):
    """Return labels needed to restore a Unity session."""

    objects: list[ObjectLabel] = Field(default_factory=list)
