"""Compare four scan strategies over the same frames, for the same output.

The single-call rows use the production prompt (prompts/scan.j2) unchanged. The
two-call baseline splits the same work across prompts/two_call_1_detect.j2 and
two_call_2_document.j2, which ask for exactly the same fields under the same limits.
Each frame is scanned on its own, with no cue list and no memory of the ones before:
re-identification is what production adds on top, and the baseline has nothing to
compare it against.

    python benchmark/run.py [--frames N]

Writes results.json next to this file.
"""
from __future__ import annotations

import argparse
import asyncio
import base64
import json
import statistics
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
sys.path.insert(0, str(REPO / "src"))

import httpx  # noqa: E402
from jinja2 import Environment, FileSystemLoader  # noqa: E402

from document_reality.config import load_config  # noqa: E402
from document_reality.llm_client import (  # noqa: E402
    JsonEntryScanner,
    VisionLlmClient,
    parse_detections,
)
from document_reality.prompts import render_scan_prompt  # noqa: E402

CAP = 5
MAX_OBJECTS = 8
MATCH_IOU = 0.5

_env = Environment(
    loader=FileSystemLoader(str(HERE / "prompts")),
    trim_blocks=True,
    lstrip_blocks=True,
)


def render(name: str, **values) -> str:
    """Render one of this benchmark's own prompts.

    Args:
        name: Template file name inside prompts/.
        **values: Template variables.

    Returns:
        Rendered prompt text.
    """
    return _env.get_template(name).render(**values).strip()


def message(client: VisionLlmClient, text: str, image: bytes) -> list[dict]:
    """Build the one user turn carrying a prompt and a frame.

    Args:
        client: Configured vision client, for the production downscaler.
        text: Rendered prompt.
        image: Encoded source frame.

    Returns:
        Chat messages ready to send.
    """
    small = client._downscale_image(image)
    url = "data:image/jpeg;base64," + base64.b64encode(small).decode("utf-8")
    return [{
        "role": "user",
        "content": [
            {"type": "text", "text": text},
            {"type": "image_url", "image_url": {"url": url}},
        ],
    }]


async def call(client: VisionLlmClient, messages: list[dict]) -> tuple[str, int]:
    """Send one non-streamed request.

    Args:
        client: Configured vision client.
        messages: Chat messages to send.

    Returns:
        The reply text and the completion tokens it cost.
    """
    payload = client._chat_payload(messages, None, client.cfg.scan_max_tokens)
    async with httpx.AsyncClient(timeout=client.cfg.timeout_s) as http:
        resp = await http.post(
            f"{client.base_url}/chat/completions", json=payload, headers=client.headers
        )
    resp.raise_for_status()
    data = resp.json()
    return (
        data["choices"][0]["message"]["content"] or "",
        (data.get("usage") or {}).get("completion_tokens", 0),
    )


def labels_of(detections: list) -> list[dict]:
    """Reduce detections to the fields every strategy must produce.

    Args:
        detections: Parsed detections.

    Returns:
        One comparable record per detection.
    """
    return [
        {
            "name": d.name,
            "box": [d.x0, d.y0, d.x1, d.y1],
            "title": d.title,
            "body": d.body,
        }
        for d in detections
    ]


async def two_calls(client: VisionLlmClient, image: bytes) -> dict:
    """Locate the objects, then describe them: two round trips.

    Args:
        client: Configured vision client.
        image: Encoded source frame.

    Returns:
        This strategy's timings, tokens and labels.
    """
    started = time.perf_counter()
    first = render("two_call_1_detect.j2", max_objects=MAX_OBJECTS)
    text, tokens_a = await call(client, message(client, first, image))
    found = parse_detections(JsonEntryScanner().feed(text))

    second = render("two_call_2_document.j2", located=found)
    text_b, tokens_b = await call(client, message(client, second, image))
    written = {d.name: d for d in parse_detections(JsonEntryScanner().feed(text_b))}
    for d in found:
        match = written.get(d.name)
        if match is not None:
            d.title, d.body = match.title, match.body

    total = time.perf_counter() - started
    return {
        "strategy": "two_calls",
        "objects": len(found),
        # Nothing is drawable until the second reply lands.
        "ttfl_s": round(total, 2),
        "total_s": round(total, 2),
        "tokens": tokens_a + tokens_b,
        "tokens_exact": True,
        "labels": labels_of(found),
    }


async def single_call(client: VisionLlmClient, image: bytes) -> dict:
    """Send the production prompt once and wait it out.

    Args:
        client: Configured vision client.
        image: Encoded source frame.

    Returns:
        This strategy's timings, tokens and labels.
    """
    started = time.perf_counter()
    prompt = render_scan_prompt(known=[], max_objects=MAX_OBJECTS)
    text, tokens = await call(client, message(client, prompt, image))
    found = parse_detections(JsonEntryScanner().feed(text))
    total = time.perf_counter() - started
    return {
        "strategy": "single_call",
        "objects": len(found),
        "ttfl_s": round(total, 2),
        "total_s": round(total, 2),
        "tokens": tokens,
        "tokens_exact": True,
        "labels": labels_of(found),
    }


async def streamed(client: VisionLlmClient, image: bytes, cap: int | None) -> dict:
    """Read the same single request as it is written, hanging up at the cap.

    Args:
        client: Configured vision client.
        image: Encoded source frame.
        cap: Objects to take before closing the response, or None for all of them.

    Returns:
        This strategy's timings, tokens and labels.
    """
    prompt = render_scan_prompt(known=[], max_objects=MAX_OBJECTS)
    scanner = JsonEntryScanner()
    found: list = []
    ttfl = None
    chars = 0
    started = time.perf_counter()
    stream = client._chat_stream(
        message(client, prompt, image), max_tokens=client.cfg.scan_max_tokens
    )
    try:
        async for piece in stream:
            chars += len(piece)
            for detection in parse_detections(scanner.feed(piece)):
                found.append(detection)
                if ttfl is None:
                    ttfl = time.perf_counter() - started
                if cap is not None and len(found) >= cap:
                    raise StopAsyncIteration
    except StopAsyncIteration:
        pass
    finally:
        await stream.aclose()

    total = time.perf_counter() - started
    return {
        "strategy": "single_call_stream" if cap is None else f"stream_cap_{cap}",
        "objects": len(found),
        "ttfl_s": round(ttfl, 2) if ttfl else None,
        "total_s": round(total, 2),
        # A stream carries no usage block, so tokens here are estimated.
        "tokens": round(chars / 4),
        "tokens_exact": False,
        "labels": labels_of(found),
    }


def iou(a: list, b: list) -> float:
    """Compute overlap between two boxes.

    Args:
        a: Box as [x0, y0, x1, y1].
        b: Box to compare it with.

    Returns:
        Intersection over union, 0.0 when either box is missing.
    """
    if None in a or None in b:
        return 0.0
    ax0, ay0, ax1, ay1 = a
    bx0, by0, bx1, by1 = b
    ix0, iy0 = max(ax0, bx0), max(ay0, by0)
    ix1, iy1 = min(ax1, bx1), min(ay1, by1)
    if ix1 <= ix0 or iy1 <= iy0:
        return 0.0
    inter = (ix1 - ix0) * (iy1 - iy0)
    union = (ax1 - ax0) * (ay1 - ay0) + (bx1 - bx0) * (by1 - by0) - inter
    return inter / union if union else 0.0


def agreement(a: list[dict], b: list[dict]) -> dict:
    """Check that both full strategies saw the same objects.

    Args:
        a: Labels from the two-call strategy.
        b: Labels from the single call.

    Returns:
        Matched pairs, unmatched names, and the share of objects that matched.
    """
    pairs = []
    taken: set[int] = set()
    for x in a:
        best, best_score = None, 0.0
        for j, y in enumerate(b):
            if j in taken:
                continue
            score = iou(x["box"], y["box"])
            if score > best_score:
                best, best_score = j, score
        if best is not None and best_score >= MATCH_IOU:
            taken.add(best)
            pairs.append({
                "two_calls": x["name"],
                "single_call": b[best]["name"],
                "iou": round(best_score, 2),
            })
    bigger = max(len(a), len(b))
    return {
        "objects_two_calls": len(a),
        "objects_single_call": len(b),
        "matched": pairs,
        "unmatched_two_calls": [
            x["name"] for x in a if not any(p["two_calls"] == x["name"] for p in pairs)
        ],
        "unmatched_single_call": [y["name"] for j, y in enumerate(b) if j not in taken],
        "overlap": round(len(pairs) / bigger, 2) if bigger else 0.0,
    }


def summarise(runs: list[dict], key: str) -> dict:
    """Average one strategy over every frame.

    Args:
        runs: Every run recorded this session.
        key: Strategy name to summarise.

    Returns:
        Mean objects, TTFL, latency and tokens for that strategy.
    """
    rows = [r for r in runs if r["strategy"] == key]
    ttfl = [r["ttfl_s"] for r in rows if r["ttfl_s"] is not None]
    return {
        "strategy": key,
        "frames": len(rows),
        "objects": round(statistics.mean(r["objects"] for r in rows), 1),
        "ttfl_s": round(statistics.mean(ttfl), 2) if ttfl else None,
        "total_s": round(statistics.mean(r["total_s"] for r in rows), 2),
        "tokens": round(statistics.mean(r["tokens"] for r in rows)),
        "tokens_exact": rows[0]["tokens_exact"],
    }


def print_table(summary: list[dict]) -> None:
    """Print the averaged results as a markdown table.

    Args:
        summary: One entry per strategy.
    """
    print("\n| Strategy | Objects | TTFL | Total | Tokens |")
    print("|---|---|---|---|---|")
    for s in summary:
        print("| {} | {} | {} | {:.1f} s | {}{} |".format(
            s["strategy"], s["objects"],
            f"{s['ttfl_s']:.1f} s" if s["ttfl_s"] else "-",
            s["total_s"],
            "" if s["tokens_exact"] else "~", s["tokens"]))


async def main() -> None:
    """Run every strategy over every frame and write results.json."""
    parser = argparse.ArgumentParser()
    parser.add_argument("--frames", type=int, default=0, help="use only the first N frames")
    args = parser.parse_args()

    frames = sorted((HERE / "frames").glob("*.jpg"))
    if args.frames:
        frames = frames[: args.frames]
    if not frames:
        print("No frames in benchmark/frames/")
        return

    cfg = load_config(REPO / "config.yaml")
    client = VisionLlmClient(cfg.llm)
    print(f"Model  : {cfg.llm.model}")
    print(f"Server : {cfg.llm.base_url}")
    print(f"Frames : {len(frames)}\n")

    runs: list[dict] = []
    checks: list[dict] = []
    for i, path in enumerate(frames, 1):
        image = path.read_bytes()
        print(f"[{i}/{len(frames)}] {path.name}")
        for coro in (
            two_calls(client, image),
            single_call(client, image),
            streamed(client, image, None),
            streamed(client, image, CAP),
        ):
            r = await coro
            r["frame"] = path.name
            runs.append(r)
            print("    {:<20} {:>2} obj   ttfl {:<8} total {:>6.2f}s   {:>5} tok".format(
                r["strategy"], r["objects"],
                f"{r['ttfl_s']}s" if r["ttfl_s"] else "-",
                r["total_s"], r["tokens"]))

        frame_runs = [r for r in runs if r["frame"] == path.name]
        check = agreement(
            next(r["labels"] for r in frame_runs if r["strategy"] == "two_calls"),
            next(r["labels"] for r in frame_runs if r["strategy"] == "single_call"),
        )
        check["frame"] = path.name
        checks.append(check)
        print(f"    same objects seen: {check['overlap']:.0%}\n")

    summary = [summarise(runs, k) for k in
               ("two_calls", "single_call", "single_call_stream", f"stream_cap_{CAP}")]
    out = {
        "measured_at": time.strftime("%Y-%m-%d %H:%M:%S"),
        "model": cfg.llm.model,
        "server": cfg.llm.base_url,
        "temperature": cfg.llm.temperature,
        "max_objects_requested": MAX_OBJECTS,
        "cap": CAP,
        "frames": len(frames),
        "agreement_mean": round(statistics.mean(c["overlap"] for c in checks), 2),
        "summary": summary,
        "agreement": checks,
        "runs": runs,
    }
    (HERE / "results.json").write_text(json.dumps(out, indent=2), encoding="utf-8")

    print_table(summary)
    print(f"\nWritten to {(HERE / 'results.json').relative_to(REPO)}")


if __name__ == "__main__":
    asyncio.run(main())
