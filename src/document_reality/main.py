import asyncio
import json
import os
import socket
import time
from contextlib import asynccontextmanager
from typing import Optional

from fastapi import BackgroundTasks, FastAPI, File, Form, HTTPException, Request, UploadFile
from fastapi.encoders import jsonable_encoder
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, JSONResponse, StreamingResponse

from document_reality.config import load_config
from document_reality import discovery
from document_reality.context import ContextBuilder
from document_reality.llm_client import LlmError, VisionLlmClient
from document_reality.logging_setup import get_logger, setup_logging

setup_logging()
log = get_logger("api")

from document_reality.scanner import SceneScanner
from document_reality.schemas import (
    AnchorBindRequest,
    AnnotationPatch,
    Entity,
    EntityContext,
    ObjectListResponse,
    ScanResponse,
    StoredAnnotation,
)
from document_reality.storage import MemoryStore

CONFIG_PATH = os.environ.get("DYR_CONFIG", "config.yaml")
log.info("Loading config from %s", CONFIG_PATH)
config = load_config(CONFIG_PATH)
store = MemoryStore(config)
log.info(
    "Store loaded -> %d objects, %d annotations (data dir: %s)",
    len(store.list_entities()),
    len(store.list_annotations()),
    config.storage.entities_dir.parent,
)
llm = VisionLlmClient(config.llm)
context_builder = ContextBuilder(config, store)
scanner = SceneScanner(config, store, llm)


def _lan_ip() -> str:
    """Best-effort LAN IP of this machine (the address the headset must use)."""
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.connect(("8.8.8.8", 80))  # no packet is actually sent
            return s.getsockname()[0]
    except OSError:
        return ""


async def _llm_keep_warm_loop() -> None:
    """Probe the model at startup and keep it loaded."""
    interval = config.llm.keep_warm_interval_s
    first = True
    while True:
        await llm.probe_generation(reason="startup check" if first else "keep-warm ping")
        first = False
        if interval <= 0:
            return
        await asyncio.sleep(interval)


@asynccontextmanager
async def _lifespan(app: FastAPI):
    """Initialize provider checks for the FastAPI application lifetime."""
    lan_ip = _lan_ip()
    log.info("=" * 70)
    log.info("Document Your Reality backend v%s READY on http://0.0.0.0:8000", app.version)
    log.info("Unity on THIS PC (Editor/XR Simulator): http://127.0.0.1:8000")
    if lan_ip:
        log.info("Unity on the HEADSET (same Wi-Fi):      http://%s:8000", lan_ip)
        log.info("  (if the headset can't connect: check Windows Firewall allows port 8000)")
    else:
        log.warning("Could not detect a LAN IP - is this machine connected to a network?")
    log.info("Unity endpoints: POST /scan | GET /objects | POST /objects/{id}/anchor")
    log.info("Every incoming request is logged below with the client IP -> if you see")
    log.info("nothing when Unity runs, the request is not reaching this process at all.")
    log.info("=" * 70)
    known = len(store.list_entities())
    if known:
        # Existing records become re-identification cues for this session.
        log.warning("=" * 70)
        log.warning("STARTING WITH %d OBJECT(S) ALREADY IN THE STORE", known)
        log.warning("They are fed to the model as 'you have already documented these'.")
        log.warning("If this is a NEW room or a NEW video, empty it first:")
        log.warning("    curl -X POST http://127.0.0.1:8000/objects/reset")
        log.warning("Deleting the JSON files in data/ by hand does NOT work while")
        log.warning("this process is running: the objects live in memory too.")
        log.warning("=" * 70)

    # Probe in the background so a cold/dead model never blocks server startup.
    keep_warm = asyncio.create_task(_llm_keep_warm_loop())
    # So a headset build does not have to be rebuilt every time this PC changes network.
    announcer = await discovery.serve(port=8000)
    yield
    keep_warm.cancel()
    if announcer is not None:
        announcer.close()
    log.info("Backend shutting down")


app = FastAPI(
    title="Document Your Reality Backend",
    version="0.5.0",
    lifespan=_lifespan,
    description=(
        "One frame in, MR labels out: the vision model detects every object, "
        "re-identifies the ones already documented, and writes their "
        "documentation. Works the same from a headset's passthrough camera "
        "and from a video file."
    ),
)
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.middleware("http")
async def log_every_request(request: Request, call_next):
    """Log one HTTP request and its response.

    Args:
        request: Incoming FastAPI request.
        call_next: Downstream request handler.

    Returns:
        Downstream response.
    """
    client_ip = request.client.host if request.client else "?"
    started = time.perf_counter()
    try:
        response = await call_next(request)
    except Exception:
        log.exception(
            "REQ %s %s from %s CRASHED after %.0f ms (unhandled exception)",
            request.method, request.url.path, client_ip,
            (time.perf_counter() - started) * 1000,
        )
        raise
    elapsed_ms = (time.perf_counter() - started) * 1000
    status = response.status_code
    logger = log.info if status < 400 else log.warning
    logger(
        "REQ %s %s from %s -> HTTP %d in %.0f ms",
        request.method, request.url.path, client_ip, status, elapsed_ms,
    )
    return response


@app.exception_handler(RequestValidationError)
async def log_validation_errors(request: Request, exc: RequestValidationError):
    """Log and return a FastAPI validation error.

    Args:
        request: Invalid incoming request.
        exc: Validation exception.

    Returns:
        HTTP 422 JSON response.
    """
    client_ip = request.client.host if request.client else "?"
    log.warning(
        "422 VALIDATION ERROR on %s %s from %s -> %s",
        request.method, request.url.path, client_ip, exc.errors(),
    )
    log.warning(
        "  (a 422 here usually means the Unity request fields don't match the "
        "endpoint: check form field names, JSON casing, missing required fields)"
    )
    # Serialize exception objects stored in validator error contexts.
    return JSONResponse(status_code=422, content={"detail": jsonable_encoder(exc.errors())})



@app.get("/health")
def health() -> dict:
    """Return backend, provider, and store status."""
    return {
        "ok": True,
        "llm_base_url": config.llm.base_url,
        "llm_model": config.llm.model,
        # Cached state of the latest real generation probe.
        "llm_state": llm.probe_state,
        "llm_probe_latency_s": round(llm.probe_latency_s, 1),
        "llm_checked_at": llm.probe_checked_at,
        "llm_error": llm.probe_error,
        "objects": len(store.list_entities()),
        "annotations": len(store.list_annotations()),
    }


@app.get("/llm/health")
async def llm_health(probe: bool = False) -> dict:
    """Cached LLM status; pass ?probe=true to force a fresh generation test."""
    if probe:
        log.info("GET /llm/health?probe=true -> running a fresh generation probe ...")
        await llm.probe_generation(reason="manual /llm/health probe")
    return {
        "llm_base_url": config.llm.base_url,
        "llm_model": config.llm.model,
        "llm_state": llm.probe_state,
        "llm_probe_latency_s": round(llm.probe_latency_s, 1),
        "llm_checked_at": llm.probe_checked_at,
        "llm_error": llm.probe_error,
    }



# Only one frame is analysed at a time.
_scan_in_flight = asyncio.Lock()


async def _update_memory_task(entity_id: str, annotation_id: str) -> None:
    """Distill one annotation into an entity's memory."""
    log.info("[background] Updating memory of object %s from annotation %s", entity_id, annotation_id)
    try:
        entity = store.get_entity(entity_id)
        annotation = store.get_annotation(annotation_id)
        memory = await llm.update_entity_memory(
            entity,
            annotation,
            max_facts=config.memory.max_facts,
            max_open_points=config.memory.max_open_points,
        )
        store.update_entity_memory(entity_id, memory)
        log.info(
            "[background] Memory of %s updated -> v%d, %d facts, %d open points",
            entity_id, memory.version, len(memory.facts), len(memory.open_points),
        )
    except Exception:
        log.exception("[background] Memory update FAILED for object %s", entity_id)


@app.post("/scan", response_model=ScanResponse)
async def scan(
    background: BackgroundTasks,
    image: UploadFile = File(...),
    source: str = Form("", description="Where the frame came from, e.g. 'video:room.mp4' or 'headset'."),
    room_id: str = Form(""),
) -> ScanResponse:
    """Analyze an uploaded frame and return resolved labels.

    Args:
        background: FastAPI background-task scheduler.
        image: Uploaded frame.
        source: Frame-source identifier.
        room_id: Optional room identifier.

    Returns:
        Scan result and resolved labels.

    Raises:
        HTTPException: If the upload is empty or inference fails.
    """
    image_bytes = await image.read()
    log.info(
        "POST /scan <- frame %.1f KB from %s",
        len(image_bytes) / 1024,
        source or "unknown source",
    )
    if not image_bytes:
        log.warning("POST /scan rejected: image upload is EMPTY (check the Unity capture code)")
        raise HTTPException(status_code=400, detail="Empty image upload")

    if _scan_in_flight.locked():
        log.info("Refused: a previous frame is still being analysed -> not queuing another LLM call")
        return ScanResponse(ok=False, reason="Busy: the previous frame is still being analysed")

    async with _scan_in_flight:
        try:
            response = await scanner.scan(image_bytes, source=source, room_id=room_id)
        except LlmError as exc:
            log.error("POST /scan FAILED at the LLM step: %s", exc)
            raise HTTPException(status_code=502, detail=str(exc)) from exc

    # Distill memory outside the response path.
    for label in response.labels:
        if label.updated:
            latest = store.latest_annotation_for_entity(label.entity_id)
            if latest is not None:
                background.add_task(_update_memory_task, label.entity_id, latest.annotation_id)
    return response


@app.post("/scan/stream")
async def scan_stream(
    image: UploadFile = File(...),
    source: str = Form("", description="Where the frame came from, e.g. 'video:room.mp4' or 'headset'."),
    room_id: str = Form(""),
) -> StreamingResponse:
    """Stream labels as newline-delimited JSON.

        Args:
            image: Uploaded frame.
            source: Frame-source identifier.
        room_id: Optional room identifier.

    Returns:
        Streaming NDJSON response.
    """
    image_bytes = await image.read()
    log.info(
        "POST /scan/stream <- frame %.1f KB from %s",
        len(image_bytes) / 1024,
        source or "unknown source",
    )
    if not image_bytes:
        log.warning("POST /scan/stream rejected: image upload is EMPTY")
        raise HTTPException(status_code=400, detail="Empty image upload")

    if _scan_in_flight.locked():
        log.info("Refused: a previous frame is still being analysed -> not queuing another LLM call")
        return StreamingResponse(
            iter([_line("done", "done", {
                "ok": False, "elapsed_s": 0.0, "count": 0,
                "reason": "Busy: the previous frame is still being analysed",
            })]),
            media_type="application/x-ndjson",
        )

    async def lines():
        """Yield NDJSON events for labels and final scan status."""
        async with _scan_in_flight:
            started = time.perf_counter()
            count = 0
            try:
                async for label in scanner.scan_stream(
                    image_bytes, source=source, room_id=room_id
                ):
                    count += 1
                    # Out of the door before the next object is even written.
                    yield _line("label", "label", jsonable_encoder(label))
                    # Distill memory after emitting the label.
                    if label.updated:
                        latest = store.latest_annotation_for_entity(label.entity_id)
                        if latest is not None:
                            _spawn(_update_memory_task(label.entity_id, latest.annotation_id))
            except LlmError as exc:
                log.error("POST /scan/stream FAILED at the LLM step: %s", exc)
                yield _line("error", "error", str(exc))
                return
            yield _line("done", "done", {
                "ok": True,
                "elapsed_s": round(time.perf_counter() - started, 2),
                "count": count,
                "reason": "" if count else "No object clearly identifiable in this frame",
            })

    return StreamingResponse(lines(), media_type="application/x-ndjson")


def _line(kind: str, field: str, value) -> str:
    """One NDJSON line. Compact and ASCII-safe: it crosses a socket that a
    Unity client reads a line at a time."""
    return json.dumps({"t": kind, field: value}, ensure_ascii=False) + "\n"


# Retain streaming tasks until completion.
_background_tasks: set[asyncio.Task] = set()


def _spawn(coro) -> None:
    """Schedule and retain a background coroutine until completion."""
    task = asyncio.create_task(coro)
    _background_tasks.add(task)
    task.add_done_callback(_background_tasks.discard)



@app.get("/objects", response_model=ObjectListResponse)
def list_objects(source: str = "") -> ObjectListResponse:
    """Everything known so far. The headset calls this once at startup to
    restore the labels of objects it has a spatial anchor for.

    Args:
        source: Restore only the objects of this frame source's world, so a run in the
            virtual room does not come up wearing the labels of the real one. Omit it to
            list the whole store.
    """
    labels = scanner.known_labels(source)
    log.info("GET /objects%s -> %d known object(s), %d anchored",
             f" (source={source})" if source else "",
             len(labels), sum(1 for lb in labels if lb.anchor_uuid))
    return ObjectListResponse(objects=labels)


@app.post("/objects/reset")
def reset_objects() -> dict:
    """Remove all entities, annotations, and captures.

    Returns:
        Counts of removed entities and annotations.
    """
    entities, annotations = store.reset()
    log.info("POST /objects/reset -> %d object(s) and %d observation(s) removed",
             entities, annotations)
    return {"ok": True, "objects_removed": entities, "annotations_removed": annotations}


@app.get("/objects/{entity_id}", response_model=EntityContext)
def get_object(entity_id: str) -> EntityContext:
    """Everything known about one object: identity, memory, full history."""
    try:
        entity = store.get_entity(entity_id)
    except KeyError as exc:
        raise HTTPException(status_code=404, detail="Object not found") from exc
    return context_builder.build(entity)


@app.post("/objects/{entity_id}/anchor", response_model=Entity)
def bind_anchor(entity_id: str, request: AnchorBindRequest) -> Entity:
    """Bind an entity to a Unity anchor.

    Args:
        entity_id: Entity to bind.
        request: Anchor data reported by Unity.

    Returns:
        Updated entity.

    Raises:
        HTTPException: If the entity does not exist.
    """
    try:
        return store.bind_anchor(
            entity_id,
            anchor_uuid=request.anchor_uuid,
            anchor_kind=request.anchor_kind,
            qr_payload=request.qr_payload,
            room_id=request.room_id,
        )
    except KeyError as exc:
        raise HTTPException(status_code=404, detail="Object not found") from exc


@app.get("/objects/by-anchor/{anchor_uuid}", response_model=EntityContext)
def get_object_by_anchor(anchor_uuid: str) -> EntityContext:
    """Resolve an object from an anchor the headset just restored or a QR payload."""
    entity = store.find_by_anchor(anchor_uuid)
    if entity is None:
        raise HTTPException(status_code=404, detail="No object bound to that anchor")
    return context_builder.build(entity)


@app.delete("/objects/{entity_id}")
def delete_object(entity_id: str) -> dict:
    """Delete an entity and its annotations."""
    try:
        store.delete_entity(entity_id)
    except KeyError as exc:
        raise HTTPException(status_code=404, detail="Object not found") from exc
    return {"deleted": entity_id}



@app.get("/annotations", response_model=list[StoredAnnotation])
def list_annotations(entity_id: Optional[str] = None) -> list[StoredAnnotation]:
    """List annotations, optionally filtered by entity."""
    if entity_id is not None:
        return store.annotations_for_entity(entity_id)
    return store.list_annotations()


@app.get("/annotations/{annotation_id}", response_model=StoredAnnotation)
def get_annotation(annotation_id: str) -> StoredAnnotation:
    """Return one stored annotation."""
    try:
        return store.get_annotation(annotation_id)
    except KeyError as exc:
        raise HTTPException(status_code=404, detail="Annotation not found") from exc


@app.patch("/annotations/{annotation_id}", response_model=StoredAnnotation)
def update_annotation(annotation_id: str, patch: AnnotationPatch) -> StoredAnnotation:
    """Apply a partial update to an annotation."""
    try:
        return store.update_annotation(annotation_id, patch)
    except KeyError as exc:
        raise HTTPException(status_code=404, detail="Annotation not found") from exc


@app.delete("/annotations/{annotation_id}")
def delete_annotation(annotation_id: str) -> dict:
    """Delete one annotation and its capture."""
    try:
        store.delete_annotation(annotation_id)
    except KeyError as exc:
        raise HTTPException(status_code=404, detail="Annotation not found") from exc
    return {"deleted": annotation_id}


@app.get("/captures/{annotation_id}")
def get_capture_image(annotation_id: str):
    """Return the JPEG captured for an annotation."""
    path = config.storage.captures_dir / f"{annotation_id}.jpg"
    if not path.exists():
        raise HTTPException(status_code=404, detail="Capture image not found")
    return FileResponse(path, media_type="image/jpeg")



def run() -> None:
    """Start the development ASGI server."""
    import uvicorn
    uvicorn.run("document_reality.main:app", host="0.0.0.0", port=8000, reload=True)
