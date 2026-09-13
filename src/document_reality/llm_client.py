from __future__ import annotations

import base64
import io
import json
import time
from collections.abc import AsyncIterator
from typing import Any

import httpx
from pydantic import BaseModel, Field, ValidationError

from document_reality.config import LlmConfig
from document_reality.logging_setup import get_logger
from document_reality.prompts import (
    KnownObjectCue,
    render_memory_prompt,
    render_scan_prompt,
)
from document_reality.schemas import (
    Entity,
    EntityMemory,
    ScanDetection,
    ScanResult,
    StoredAnnotation,
)

log = get_logger("llm")

# How far from square a frame may be before its long side is capped too.
_MAX_ASPECT = 2.0


class LlmError(RuntimeError):
    """Report a vision-provider or response error."""
    pass


class MemoryDraft(BaseModel):
    """Flat schema the model fills when updating an entity's memory."""

    summary: str = ""
    facts: list[str] = Field(default_factory=list)
    open_points: list[str] = Field(default_factory=list)


class VisionLlmClient:
    """Call an OpenAI-compatible vision provider."""

    def __init__(self, config: LlmConfig):
        """Initialize the client from provider configuration."""
        self.cfg = config
        self.base_url = config.base_url.rstrip("/")
        self.headers = {"Content-Type": "application/json"}
        # Cache the latest generation probe for GET /health.
        self.probe_state: str = "untested"  # untested | up | down
        self.probe_latency_s: float = 0.0
        self.probe_checked_at: str = ""
        self.probe_error: str = ""
        if config.api_key and config.api_key != "none":
            self.headers["Authorization"] = f"Bearer {config.api_key}"
        log.info(
            "LLM client configured -> base_url=%s model=%s timeout=%.0fs auth=%s",
            self.base_url,
            config.model,
            config.timeout_s,
            "yes" if "Authorization" in self.headers else "no",
        )


    async def probe_generation(self, reason: str = "startup") -> bool:
        """Test whether the configured model can generate a response.

        Args:
            reason: Description included in diagnostic logs.

        Returns:
            Whether the generation probe succeeded.
        """
        log.info(
            "LLM PROBE (%s) -> asking '%s' for a 1-word completion "
            "(a cold model loads now: first probe can take minutes) ...",
            reason,
            self.cfg.model,
        )
        started = time.perf_counter()
        try:
            await self._chat(
                [{"role": "user", "content": "Reply with the single word: OK"}],
                response_format=None,
                max_tokens=10,
            )
        except (LlmError, httpx.HTTPError) as exc:
            elapsed = time.perf_counter() - started
            self.probe_state = "down"
            self.probe_latency_s = elapsed
            self.probe_checked_at = time.strftime("%Y-%m-%d %H:%M:%S")
            self.probe_error = f"{type(exc).__name__}: {str(exc)[:300]}"
            log.error("!" * 70)
            log.error(
                "LLM MODEL IS DOWN: '%s' did NOT answer the test completion (%.1fs, %s)",
                self.cfg.model,
                elapsed,
                type(exc).__name__,
            )
            log.error(
                "EVERY capture will fail until the model answers. Check the machine "
                "at %s: is Ollama running? is the model pulled? try: ollama run %s",
                self.base_url,
                self.cfg.model,
            )
            log.error("!" * 70)
            return False

        elapsed = time.perf_counter() - started
        self.probe_state = "up"
        self.probe_latency_s = elapsed
        self.probe_checked_at = time.strftime("%Y-%m-%d %H:%M:%S")
        self.probe_error = ""
        log.info("=" * 70)
        log.info(
            "LLM MODEL IS UP: '%s' answered the test completion in %.1fs",
            self.cfg.model,
            elapsed,
        )
        if elapsed > 30:
            log.warning(
                "The model is SLOW (%.0fs for ~1 token): vision captures will take "
                "several minutes. Consider a smaller model or a faster machine.",
                elapsed,
            )
        log.info("=" * 70)
        return True


    async def scan_frame(
        self,
        image_bytes: bytes,
        known: list[KnownObjectCue] | None = None,
        max_objects: int = 8,
    ) -> ScanResult:
        """Analyze one frame and return its detections.

        Args:
            image_bytes: Encoded source image.
            known: Objects available for re-identification.
            max_objects: Maximum detections to request.

        Returns:
            Validated scan result, never longer than "max_objects".
        """
        messages = self._scan_messages(image_bytes, known, max_objects)
        result = await self._ask_for_objects(messages, "Scan")
        if len(result.objects) > max_objects:
            log.info(
                "Scan returned %d object(s) over a cap of %d -> dropping the tail: %s",
                len(result.objects), max_objects,
                ", ".join(o.name or o.known_id or "?" for o in result.objects[max_objects:]),
            )
            result.objects = result.objects[:max_objects]
        log.info(
            "Scan parsed -> %d object(s): %s",
            len(result.objects),
            ", ".join(
                f"{o.name}{'=' + o.known_id if o.known_id else ' (new)'}" for o in result.objects
            )
            or "(none)",
        )
        return result

    async def scan_frame_stream(
        self,
        image_bytes: bytes,
        known: list[KnownObjectCue] | None = None,
        max_objects: int = 8,
    ) -> AsyncIterator[ScanDetection]:
        """Stream detections from one frame, and hang up once there are enough.

        Hanging up is where the cap pays for itself. Asked for five the model writes ten
        anyway, and trimming the list after the answer arrives saves nothing: those tokens were
        already generated and waited for. Closing the response at the fifth object stops the
        generation - on a ten-object frame of 11.3s, about half the wait.

        Args:
            image_bytes: Encoded source image.
            known: Objects available for re-identification.
            max_objects: Detections to take before hanging up.

        Yields:
            Each validated detection as soon as its JSON entry completes.
        """
        messages = self._scan_messages(image_bytes, known, max_objects)
        scanner = JsonEntryScanner()
        started = time.perf_counter()
        cap = max(1, max_objects)
        count = 0
        enough = False

        # Held by name so the response can be closed on purpose. Breaking out of an `async
        # for` leaves the generator to be closed whenever it is collected, and until it is,
        # the model is still generating into a socket nobody reads.
        stream = self._chat_stream(
            messages,
            max_tokens=self.cfg.scan_max_tokens,
            response_format=self._scan_format(),
        )
        try:
            async for piece in stream:
                for detection in parse_detections(scanner.feed(piece)):
                    count += 1
                    if count == 1:
                        log.info(
                            "Stream -> FIRST object after %.1fs ('%s')",
                            time.perf_counter() - started,
                            detection.name or detection.known_id,
                        )
                    yield detection
                    if count >= cap:
                        enough = True
                        break
                if enough:
                    break
        finally:
            await stream.aclose()

        elapsed = time.perf_counter() - started
        if enough:
            log.info("Stream hung up at the cap of %d after %.1fs", cap, elapsed)
        elif count:
            log.info("Stream done in %.1fs -> %d object(s)", elapsed, count)
        else:
            # Distinguish an empty result from an invalid response.
            text = scanner.text.strip()
            log.info(
                "Stream done in %.1fs -> no object. Raw reply (%d chars): %s",
                elapsed, len(text), text[:300] or "(empty)",
            )

    def _scan_format(self) -> dict[str, Any] | None:
        """The schema a scan reply is constrained to, or None when unconstrained."""
        if not self.cfg.scan_constrained:
            return None
        return _json_schema_format("scan_result", ScanResult)

    async def _ask_for_objects(self, messages: list[dict[str, Any]], what: str) -> ScanResult:
        """Ask, and ask once more if the answer cannot be read.

        A reply that is not JSON is almost never a fact about the frame: it is the model
        having a bad turn, and the same request usually comes back clean. Giving up instead
        costs the wearer the whole frame, which is seconds they already paid for.
        """
        for attempt in (1, 2):
            raw_text = await self._chat(
                messages,
                response_format=self._scan_format(),
                max_tokens=self.cfg.scan_max_tokens,
            )
            try:
                return _parse_scan_result(raw_text)
            except LlmError:
                if attempt == 2:
                    raise
                log.warning(
                    "%s answer could not be read (%d chars, starts with %s) -> asking once more",
                    what, len(raw_text), repr(raw_text[:120]),
                )
        raise LlmError("unreachable")   # the loop either returns or raises

    def _scan_messages(
        self,
        image_bytes: bytes,
        known: list[KnownObjectCue] | None,
        max_objects: int,
    ) -> list[dict[str, Any]]:
        """The one request both scan paths send, built in one place so the
        streaming answer can never come from a different prompt."""
        image_bytes = self._downscale_image(image_bytes)
        image_b64 = base64.b64encode(image_bytes).decode("utf-8")
        known = known or []
        system_prompt = render_scan_prompt(known=known, max_objects=max_objects)
        log.info(
            "Scan request -> image=%.1f KB, known_objects=%d, max_objects=%d",
            len(image_bytes) / 1024,
            len(known),
            max_objects,
        )
        return [
            {"role": "system", "content": system_prompt},
            {
                "role": "user",
                "content": [
                    {
                        "type": "image_url",
                        "image_url": {"url": f"data:image/jpeg;base64,{image_b64}"},
                    },
                    {"type": "text",
                     "text": "Detect, re-identify and document the objects in this frame."},
                ],
            },
        ]


    async def update_entity_memory(
        self,
        entity: Entity,
        annotation: StoredAnnotation,
        max_facts: int,
        max_open_points: int,
    ) -> EntityMemory:
        """Merge a new observation into distilled entity memory.

        Args:
            entity: Entity whose memory is updated.
            annotation: Observation to merge.
            max_facts: Maximum retained facts.
            max_open_points: Maximum retained open points.

        Returns:
            Updated memory draft.
        """
        prompt = render_memory_prompt(entity, annotation, max_facts, max_open_points)
        log.info(
            "Memory update request -> entity=%s ('%s'), prompt_chars=%d",
            entity.entity_id,
            entity.display_name(),
            len(prompt),
        )
        try:
            raw_text = await self._chat(
                [{"role": "user", "content": prompt}],
                _json_schema_format("entity_memory", MemoryDraft),
            )
            draft = _parse_model(raw_text, MemoryDraft)
            log.info(
                "Memory update parsed -> facts=%d open_points=%d summary_chars=%d",
                len(draft.facts),
                len(draft.open_points),
                len(draft.summary),
            )
        except (LlmError, httpx.HTTPError) as exc:
            log.warning(
                "Memory update via LLM failed (%s: %s) -> using deterministic fallback merge",
                type(exc).__name__,
                str(exc)[:200],
            )
            draft = _fallback_memory_merge(entity, annotation, max_facts)

        return EntityMemory(
            summary=draft.summary.strip(),
            facts=[f.strip() for f in draft.facts if f.strip()][:max_facts],
            open_points=[p.strip() for p in draft.open_points if p.strip()][:max_open_points],
        )

    def _downscale_image(self, image_bytes: bytes) -> bytes:
        """Resize oversized captures before upload.

        Args:
            image_bytes: Encoded source image.

        Returns:
            Original or resized JPEG bytes.
        """
        if self.cfg.max_image_px <= 0:
            return image_bytes
        try:
            from PIL import Image

            with Image.open(io.BytesIO(image_bytes)) as im:
                w, h = im.size
                short, long_ = min(w, h), max(w, h)
                scale = min(
                    self.cfg.max_image_px / short,
                    self.cfg.max_image_px * _MAX_ASPECT / long_,
                )
                if scale >= 1.0:
                    return image_bytes    # already smaller: never upscale, it adds nothing
                new_size = (max(1, round(w * scale)), max(1, round(h * scale)))
                resized = im.convert("RGB").resize(new_size, Image.LANCZOS)
            buf = io.BytesIO()
            resized.save(buf, "JPEG", quality=self.cfg.image_jpeg_quality)
            out = buf.getvalue()
            log.info(
                "Capture downscaled for the model: %dx%d (%.1f KB) -> %dx%d (%.1f KB)",
                w, h, len(image_bytes) / 1024,
                new_size[0], new_size[1], len(out) / 1024,
            )
            return out
        except Exception as exc:  # a bad image must not kill the request here
            log.warning("Image downscale failed (%s: %s) -> sending original", type(exc).__name__, exc)
            return image_bytes


    def _chat_payload(
        self,
        messages: list[dict[str, Any]],
        response_format: dict[str, Any] | None,
        max_tokens: int | None,
    ) -> dict[str, Any]:
        """Build a provider-compatible chat request."""
        payload: dict[str, Any] = {
            "model": self.cfg.model,
            "messages": messages,
            "max_tokens": max_tokens if max_tokens is not None else self.cfg.max_tokens,
            "temperature": self.cfg.temperature,
            "stream": False,
        }
        if response_format:
            payload["response_format"] = response_format
        if self.cfg.disable_thinking:
            # Prevent reasoning content from exhausting the response budget.
            payload["chat_template_kwargs"] = {"enable_thinking": False}
        return payload

    async def _chat(
        self,
        messages: list[dict[str, Any]],
        response_format: dict[str, Any] | None,
        max_tokens: int | None = None,
    ) -> str:
        """Send a non-streaming chat-completion request."""
        payload = self._chat_payload(messages, response_format, max_tokens)
        url = f"{self.base_url}/chat/completions"
        payload_kb = len(json.dumps(payload)) / 1024
        log.info("POST %s -> model=%s payload=%.1f KB ... (waiting for the model)", url, self.cfg.model, payload_kb)
        started = time.perf_counter()

        try:
            async with httpx.AsyncClient(timeout=self.cfg.timeout_s) as client:
                resp = await client.post(url, json=payload, headers=self.headers)
        except httpx.ConnectError as exc:
            log.error("CONNECTION FAILED to %s: %s. Is the model server running?", url, exc)
            raise LlmError(f"Cannot connect to LLM at {url}: {exc}") from exc
        except httpx.TimeoutException as exc:
            elapsed = time.perf_counter() - started
            log.error("LLM TIMEOUT after %.1fs (limit %.0fs) at %s", elapsed, self.cfg.timeout_s, url)
            raise LlmError(f"LLM timed out after {elapsed:.1f}s: {exc}") from exc

        elapsed = time.perf_counter() - started
        if resp.status_code >= 400:
            log.error("LLM answered HTTP %s in %.1fs: %s", resp.status_code, elapsed, resp.text[:300])
            raise LlmError(f"LLM request failed: {resp.status_code} {resp.text[:500]}")

        try:
            data = resp.json()
            content = data["choices"][0]["message"]["content"]
        except (ValueError, KeyError, IndexError, TypeError) as exc:
            log.error("LLM response has unexpected shape (%.1fs): %s", elapsed, resp.text[:300])
            raise LlmError(f"Unexpected LLM response shape: {resp.text[:500]}") from exc

        usage = data.get("usage") or {}
        log.info(
            "LLM response OK in %.1fs -> %d chars (tokens: prompt=%s completion=%s)",
            elapsed,
            len(content or ""),
            usage.get("prompt_tokens", "?"),
            usage.get("completion_tokens", "?"),
        )
        log.debug("LLM raw output: %s", (content or "")[:1000])
        return content


    async def _chat_stream(
        self,
        messages: list[dict[str, Any]],
        max_tokens: int | None = None,
        response_format: dict[str, Any] | None = None,
    ) -> AsyncIterator[str]:
        """Stream text from a chat-completion request.

        Args:
            payload: Provider request body.

        Yields:
            Text fragments in arrival order.
        """
        payload = self._chat_payload(messages, response_format, max_tokens)
        payload["stream"] = True
        url = f"{self.base_url}/chat/completions"
        log.info("POST %s (stream) -> model=%s ...", url, self.cfg.model)
        started = time.perf_counter()
        chars = 0

        try:
            async with httpx.AsyncClient(timeout=self.cfg.timeout_s) as client:
                async with client.stream(
                    "POST", url, json=payload, headers=self.headers
                ) as resp:
                    if resp.status_code >= 400:
                        body = (await resp.aread()).decode("utf-8", "replace")
                        log.error("LLM answered HTTP %s to the stream: %s",
                                  resp.status_code, body[:300])
                        raise LlmError(f"LLM request failed: {resp.status_code} {body[:500]}")

                    async for line in resp.aiter_lines():
                        if not line.startswith("data:"):
                            continue
                        data = line[len("data:") :].strip()
                        if not data or data == "[DONE]":
                            continue
                        try:
                            chunk = json.loads(data)
                        except ValueError:
                            log.debug("Skipping an unreadable stream chunk: %s", data[:200])
                            continue
                        choices = chunk.get("choices") or []
                        if not choices:
                            continue
                        piece = (choices[0].get("delta") or {}).get("content")
                        if piece:
                            chars += len(piece)
                            yield piece
        except httpx.ConnectError as exc:
            log.error("CONNECTION FAILED to %s: %s. Is the model server running?", url, exc)
            raise LlmError(f"Cannot connect to LLM at {url}: {exc}") from exc
        except httpx.TimeoutException as exc:
            elapsed = time.perf_counter() - started
            log.error("LLM TIMEOUT after %.1fs (limit %.0fs) at %s",
                      elapsed, self.cfg.timeout_s, url)
            raise LlmError(f"LLM timed out after {elapsed:.1f}s: {exc}") from exc

        log.info("LLM stream closed after %.1fs -> %d chars",
                 time.perf_counter() - started, chars)


def _json_schema_format(name: str, model: type[BaseModel]) -> dict[str, Any]:
    """Build the OpenAI structured-output descriptor."""
    return {
        "type": "json_schema",
        "json_schema": {"name": name, "schema": model.model_json_schema()},
    }


def _fallback_memory_merge(entity: Entity, annotation: StoredAnnotation, max_facts: int) -> MemoryDraft:
    """Merge an annotation locally when the model is unavailable."""
    facts = list(entity.memory.facts)
    new_fact = annotation.annotation.title or annotation.annotation.object_name
    observation = annotation.annotation.body
    if observation:
        new_fact = f"{new_fact}: {observation}"[:120]
    if new_fact and new_fact not in facts:
        facts.append(new_fact)
    summary = entity.memory.summary or observation or annotation.annotation.title
    return MemoryDraft(
        summary=summary,
        facts=facts[-max_facts:],
        open_points=list(entity.memory.open_points),
    )


class JsonEntryScanner:
    """Incrementally extract complete objects from a streamed JSON array."""

    def __init__(self) -> None:
        """Initialize incremental JSON parsing state."""
        self._buffer: list[str] = []
        self._length = 0
        self._pending = ""      # text scanned but not yet part of a closed entry
        self._depth = 0
        self._start = -1
        self._in_array = False
        self._in_string = False
        self._escaped = False

    def feed(self, chunk: str) -> list[str]:
        """Add newly arrived text; return the entries that just became complete."""
        if not chunk:
            return []
        self._buffer.append(chunk)
        self._length += len(chunk)
        found: list[str] = []
        base = len(self._pending)
        text = self._pending + chunk
        for i, ch in enumerate(text[base:], start=base):
            if self._in_string:
                if self._escaped:
                    self._escaped = False
                elif ch == "\\":
                    self._escaped = True
                elif ch == '"':
                    self._in_string = False
                continue
            if ch == '"':
                self._in_string = True
            elif ch == "[":
                self._in_array = True
            elif self._in_array and ch == "{":
                if self._depth == 0:
                    self._start = i
                self._depth += 1
            elif self._in_array and ch == "}":
                self._depth -= 1
                if self._depth == 0 and self._start >= 0:
                    found.append(text[self._start : i + 1])
                    self._start = -1
                elif self._depth < 0:
                    self._depth = 0
        # Keep only what an unfinished entry still needs, so the window does not grow with the answer.
        if self._start >= 0:
            self._pending = text[self._start :]
            self._start = 0
        else:
            self._pending = ""
        return found

    @property
    def text(self) -> str:
        """Everything fed in so far, for logging a reply that would not parse."""
        return "".join(self._buffer)


def parse_detections(entries: list[str]) -> list[ScanDetection]:
    """Turn complete JSON entries into detections, dropping the unusable ones."""
    detections: list[ScanDetection] = []
    for entry in entries:
        try:
            detections.append(ScanDetection.model_validate_json(entry))
        except (ValidationError, json.JSONDecodeError):
            log.debug("Skipping an entry that is not a usable detection: %s", entry[:200])
    return detections


def _parse_scan_result(raw_text: str) -> ScanResult:
    """Parse a scan response and salvage complete detections."""
    try:
        return _parse_model(raw_text, ScanResult)
    except LlmError:
        pass

    objects = parse_detections(JsonEntryScanner().feed(raw_text))
    if not objects:
        raise LlmError(f"Invalid ScanResult JSON. Raw: {raw_text[:1000]}")
    # The tail is the evidence: a budget that ran out ends mid-token, a model that added
    # commentary ends with prose, and the two need opposite fixes.
    log.warning(
        "Scan answer was truncated or malformed -> salvaged %d complete object(s). "
        "%d chars, ends with: %s",
        len(objects), len(raw_text), repr(raw_text[-160:]),
    )
    return ScanResult(objects=objects)


def _parse_model(raw_text: str, model: type[BaseModel]):
    """Validate the first JSON value found in model output."""
    try:
        return model.model_validate_json(raw_text)
    except (ValidationError, json.JSONDecodeError):
        pass
    try:
        value = _extract_first_json_value(raw_text)
        # A bare array IS the answer: a model may drop the "objects" wrapper and fence the
        # reply in markdown, with the payload inside perfectly usable.
        if isinstance(value, list) and "objects" in model.model_fields:
            value = {"objects": value}
        return model.model_validate(value)
    except Exception as exc:
        raise LlmError(f"Invalid {model.__name__} JSON. Raw: {raw_text[:1000]}") from exc


def _extract_first_json_value(text: str) -> Any:
    """Find the JSON payload inside whatever the model wrapped it in.

    Markdown fences and a leading sentence are not errors on the model's part: they are
    how most chat models answer, and the payload inside them is usually perfect.
    """
    cleaned = "\n".join(
        line for line in text.splitlines() if not line.strip().startswith("```")
    ).strip()

    opens = [where for where in (cleaned.find("{"), cleaned.find("[")) if where >= 0]
    if not opens:
        raise ValueError("No JSON object or array found in model output")
    start = min(opens)
    end = cleaned.rfind("}" if cleaned[start] == "{" else "]")
    if end <= start:
        raise ValueError("JSON payload never closes in model output")
    return json.loads(cleaned[start : end + 1])
