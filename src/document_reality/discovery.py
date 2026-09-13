"""Tell a headset on the same network where this backend is.

A build carries the backend's address baked into its scene, and that address is wrong the
moment the PC changes network: another Wi-Fi, a new DHCP lease, a demo in a room that is
not this one. Rebuilding an APK to change four numbers is not something to do with an
audience waiting.

So the headset shouts one UDP datagram on the LAN and this answers with the address to use.
No dependency, no configuration, and the address baked into the build stays as the fallback
for a network that blocks broadcast.

The reply is worked out PER PROBE rather than taken from one lookup at startup, because this
machine has more than one address: the LAN one the headset can reach, and a Tailscale one it
cannot. Answering with the wrong one sends the headset somewhere it can never connect, which
looks exactly like a backend that is down.
"""

from __future__ import annotations

import asyncio
import socket

from document_reality.logging_setup import get_logger

log = get_logger("discovery")

PORT = 8001
PROBE = b"DYR?"
REPLY = b"DYR "


def address_reachable_from(peer: str) -> str:
    """The address of this machine that "peer" can reach it on.

    Args:
        peer: The address the probe arrived from.

    Returns:
        The local address on the interface that routes to that peer, or "".
    """
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
            probe.connect((peer, 9))  # discard port; no packet is actually sent
            return probe.getsockname()[0]
    except OSError:
        return ""


class _Responder(asyncio.DatagramProtocol):
    """Answer probes with the address the asker can use."""

    def __init__(self, port: int):
        self.port = port
        self.transport: asyncio.DatagramTransport | None = None

    def connection_made(self, transport) -> None:
        self.transport = transport

    def datagram_received(self, data: bytes, addr: tuple[str, int]) -> None:
        if data.strip() != PROBE or self.transport is None:
            return
        local = address_reachable_from(addr[0])
        if not local:
            log.warning("A headset at %s asked where the backend is, and no route back to it "
                        "could be worked out -> not answering", addr[0])
            return
        url = f"http://{local}:{self.port}"
        log.info("Headset at %s asked where the backend is -> answering %s", addr[0], url)
        self.transport.sendto(REPLY + url.encode("utf-8"), addr)


async def serve(port: int = 8000) -> asyncio.DatagramTransport | None:
    """Start answering discovery probes.

    Args:
        port: The port the HTTP backend itself listens on, which is what is advertised.

    Returns:
        The transport to close on shutdown, or None when the socket could not be opened.
    """
    loop = asyncio.get_running_loop()
    try:
        transport, _ = await loop.create_datagram_endpoint(
            lambda: _Responder(port),
            local_addr=("0.0.0.0", PORT),
            allow_broadcast=True,
        )
    except OSError as error:
        log.warning(
            "Discovery is OFF: UDP %d could not be opened (%s). Nothing else breaks - a "
            "headset just has to use the address baked into its build.", PORT, error,
        )
        return None

    log.info("Discovery listening on UDP %d: a headset that asks is told how to reach us", PORT)
    return transport
