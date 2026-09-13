"""Check a vision model before putting it behind the backend.

Three things in order: the endpoint serves the configured model, the model generates, and
its boxes land on the right objects in the right coordinate order.

    python scripts/check_model.py
"""

from __future__ import annotations

import base64
import sys
from io import BytesIO
from pathlib import Path

import httpx
from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from document_reality.config import AppConfig, load_config  # noqa: E402
from document_reality.llm_client import _parse_scan_result  # noqa: E402

OK = "  OK  "
BAD = " FAIL "

GROUNDING_PROMPT = (
    'Locate every coloured shape in the image. Reply with ONLY JSON: '
    '{"objects":[{"name":"...","x0":0,"y0":0,"x1":0,"y1":0}]} '
    "where the four edges are NAMED integers 0-1000: x across the width from the left, "
    "y down the height from the top."
)


def grounding_image() -> bytes:
    """Draw the test frame: a red square top-right, a blue circle bottom-left.

    The shapes sit on the ANTI-diagonal, so a model answering [y0, x0, y1, x1] where
    [x0, y0, x1, y1] was asked for reports each shape in the other's corner, which the
    grounding check reads as a transposition.

    Returns:
        Encoded JPEG bytes.
    """
    image = Image.new("RGB", (640, 480), (245, 245, 245))
    draw = ImageDraw.Draw(image)
    draw.rectangle([400, 40, 600, 200], fill=(200, 30, 30))
    draw.ellipse([40, 280, 240, 440], fill=(30, 60, 200))
    buffer = BytesIO()
    image.save(buffer, format="JPEG", quality=90)
    return buffer.getvalue()


def corner_of(center: tuple[float, float]) -> str:
    """Name the quadrant a normalized center falls in."""
    cx, cy = center
    if cx == 0.5 or cy == 0.5:
        return "middle"
    return ("top-" if cy < 0.5 else "bottom-") + ("left" if cx < 0.5 else "right")


class Provider:
    """Send chat completions to the configured provider."""

    def __init__(self, config: AppConfig):
        """Initialize from application configuration."""
        self.cfg = config.llm
        self.base_url = self.cfg.base_url.rstrip("/")
        self.headers = {"Content-Type": "application/json"}
        if self.cfg.api_key and self.cfg.api_key != "none":
            self.headers["Authorization"] = f"Bearer {self.cfg.api_key}"

    def models(self) -> list[str]:
        """Return the model ids the endpoint serves."""
        response = httpx.get(f"{self.base_url}/models", headers=self.headers, timeout=10)
        response.raise_for_status()
        return [entry.get("id", "?") for entry in response.json().get("data", [])]

    def chat(self, messages: list[dict], max_tokens: int) -> str:
        """Send one chat-completion request and return its text."""
        payload = {
            "model": self.cfg.model,
            "messages": messages,
            "max_tokens": max_tokens,
            "temperature": 0.1,
            "stream": False,
        }
        if self.cfg.disable_thinking:
            payload["chat_template_kwargs"] = {"enable_thinking": False}
        response = httpx.post(
            f"{self.base_url}/chat/completions",
            json=payload, headers=self.headers, timeout=self.cfg.timeout_s,
        )
        response.raise_for_status()
        return response.json()["choices"][0]["message"]["content"] or ""

    def describe_image(self, image_bytes: bytes, max_tokens: int) -> str:
        """Ask the model to locate the shapes in an image."""
        encoded = base64.b64encode(image_bytes).decode()
        return self.chat(
            [
                {"role": "system", "content": GROUNDING_PROMPT},
                {"role": "user", "content": [
                    {"type": "image_url",
                     "image_url": {"url": f"data:image/jpeg;base64,{encoded}"}},
                    {"type": "text", "text": "Locate the shapes."},
                ]},
            ],
            max_tokens,
        )


def check_endpoint(provider: Provider) -> bool:
    """Report whether the endpoint answers and serves the configured model."""
    try:
        available = provider.models()
    except Exception as exc:
        print(f"[{BAD}] cannot reach {provider.base_url}: {type(exc).__name__}: {exc}")
        print("        Is the model server running, and the address right from this machine?")
        return False

    if provider.cfg.model in available:
        print(f"[{OK}] endpoint reachable, model is listed")
        return True
    print(f"[{BAD}] endpoint reachable, but '{provider.cfg.model}' is not served")
    print(f"        Listed: {', '.join(available[:8]) or '(none)'}")
    return False


def check_generation(provider: Provider) -> bool:
    """Report whether the model generates text."""
    try:
        reply = provider.chat([{"role": "user", "content": "Reply with the single word: OK"}], 10)
    except Exception as exc:
        print(f"[{BAD}] the model did not answer: {type(exc).__name__}: {exc}")
        print("        A listed model may still be loading; a cold start can take minutes.")
        return False
    print(f"[{OK}] the model answers ({reply.strip()[:40]!r})")
    return True


def check_grounding(provider: Provider) -> bool:
    """Report whether the model's boxes land on the shapes, in the asked-for axis order."""
    try:
        raw = provider.describe_image(grounding_image(), provider.cfg.scan_max_tokens)
    except Exception as exc:
        print(f"[{BAD}] the vision call failed: {type(exc).__name__}: {exc}")
        print("        A text-only model cannot drive this system: labels need boxes.")
        return False

    try:
        result = _parse_scan_result(raw)
    except Exception:
        print(f"[{BAD}] the answer was not usable JSON:")
        print("        " + raw[:300].replace("\n", "\n        "))
        return False

    if not result.objects:
        print(f"[{BAD}] nothing found in a frame holding two large plain shapes")
        print("        Detection this weak will not label a real room.")
        return False

    print(f"[{OK}] the answer parsed: {len(result.objects)} object(s)")

    placed = 0
    transposed = 0
    for detection in result.objects:
        corner = corner_of(detection.box().center)
        name = detection.name.lower()
        is_red = "red" in name or "square" in name
        is_blue = "blue" in name or "circle" in name

        correct = (is_red and corner == "top-right") or (is_blue and corner == "bottom-left")
        # Both shapes mirrored across the anti-diagonal: x and y arrived the other way round.
        swapped = (is_red and corner == "bottom-left") or (is_blue and corner == "top-right")
        placed += correct
        transposed += swapped

        cx, cy = detection.box().center
        mark = "OK" if correct else "XY" if swapped else " ·"
        print(f"        {mark} {detection.name:22s} centre ({cx:.2f}, {cy:.2f}) -> {corner}")

    if transposed and transposed >= placed:
        print(f"[{BAD}] the coordinates come back TRANSPOSED: the red square was reported")
        print("        where the blue circle is, and the other way round. This model answers")
        print("        x and y in the opposite order to the one it was asked for, so every")
        print("        label in a frame would sit on a different object - and the reply would")
        print("        look valid throughout. Read the axes in ScanDetection.box().")
        return False

    if not placed:
        print(f"[{BAD}] boxes came back, but not where the shapes are")
        print("        Labels would anchor to the wrong place. Use a model with real grounding.")
        return False

    print(f"[{OK}] grounding works: the boxes land on the right shapes, in the right order")
    return True


def main() -> int:
    """Run the checks in order, stopping at the first one that fails.

    Returns:
        Process exit status: 0 when the model can drive the system.
    """
    provider = Provider(load_config())
    print(f"Endpoint: {provider.base_url}")
    print(f"Model:    {provider.cfg.model}\n")

    for check in (check_endpoint, check_generation, check_grounding):
        if not check(provider):
            return 1

    print("\nThis model can drive the system.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
