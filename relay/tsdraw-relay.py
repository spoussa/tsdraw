#!/usr/bin/env python3
"""TSDraw relay: forwards drawing messages between TSDraw.exe instances.

Run it next to your TeamSpeak server and open its TCP port (default 9989):

    python3 tsdraw-relay.py [--port 9989] [--password SECRET]

Standard library only. The relay never sees drawings in the clear: every stream
gets its own room name and encryption key derived from the TeamSpeak stream id,
which only people in that TeamSpeak stream know.

Wire format: [u32 big-endian length L][u8 type][L-1 bytes payload]
"""
from __future__ import annotations

import argparse
import asyncio
import logging
import struct
import time

HELLO, JOIN, LEAVE, PUB, MSG, PING, PONG, ERR = range(1, 9)
MAGIC = b"TSDRAW1\n"
MAX_FRAME = 64 * 1024
MAX_TOPICS = 32
RATE, BURST = 200.0, 400.0          # messages per second per connection
MAX_BUFFERED = 1 << 20              # drop messages for receivers this far behind

rooms: dict[bytes, set[Conn]] = {}
connected = 0
log = logging.getLogger("tsdraw-relay")


def frame(kind: int, payload: bytes = b"") -> bytes:
    return struct.pack(">IB", len(payload) + 1, kind) + payload


class Conn:
    def __init__(self, writer: asyncio.StreamWriter):
        self.writer = writer
        self.topics: set[bytes] = set()
        self.tokens = BURST
        self.refill = time.monotonic()
        self.peer = writer.get_extra_info("peername")

    def send(self, data: bytes) -> None:
        if self.writer.is_closing():
            return
        if self.writer.transport.get_write_buffer_size() > MAX_BUFFERED:
            return
        self.writer.write(data)

    def allow(self) -> bool:
        now = time.monotonic()
        self.tokens = min(BURST, self.tokens + (now - self.refill) * RATE)
        self.refill = now
        if self.tokens < 1:
            return False
        self.tokens -= 1
        return True


async def read_frame(reader: asyncio.StreamReader, timeout: float) -> tuple[int, bytes]:
    hdr = await asyncio.wait_for(reader.readexactly(4), timeout)
    (length,) = struct.unpack(">I", hdr)
    if not 1 <= length <= MAX_FRAME:
        raise ValueError(f"bad frame length {length}")
    body = await asyncio.wait_for(reader.readexactly(length), timeout)
    return body[0], body[1:]


async def handle(reader: asyncio.StreamReader, writer: asyncio.StreamWriter, password: bytes) -> None:
    global connected
    conn = Conn(writer)
    joined = False
    try:
        kind, payload = await read_frame(reader, 10)
        if kind != HELLO or not payload.startswith(MAGIC):
            return
        if payload[len(MAGIC):] != password:
            writer.write(frame(ERR, b"wrong relay password"))
            await writer.drain()
            return
        writer.write(frame(HELLO, b"OK"))
        joined = True
        connected += 1
        log.info("+ %s (%d connected)", conn.peer, connected)

        while True:
            kind, payload = await read_frame(reader, 90)
            if kind == PING:
                conn.send(frame(PONG))
            elif kind in (JOIN, LEAVE):
                topic = payload
                if not 0 < len(topic) <= 64:
                    continue
                if kind == JOIN and len(conn.topics) < MAX_TOPICS and topic not in conn.topics:
                    conn.topics.add(topic)
                    rooms.setdefault(topic, set()).add(conn)
                elif kind == LEAVE and topic in conn.topics:
                    conn.topics.discard(topic)
                    room = rooms.get(topic)
                    if room is not None:
                        room.discard(conn)
                        if not room:
                            del rooms[topic]
            elif kind == PUB:
                if not payload:
                    continue
                n = payload[0]
                topic = payload[1:1 + n]
                if len(topic) != n or topic not in conn.topics or not conn.allow():
                    continue
                out = frame(MSG, payload)
                for other in rooms.get(topic, ()):
                    if other is not conn:
                        other.send(out)
            await writer.drain()
    except (asyncio.IncompleteReadError, asyncio.TimeoutError, ConnectionError, ValueError):
        pass
    finally:
        for topic in conn.topics:
            room = rooms.get(topic)
            if room is not None:
                room.discard(conn)
                if not room:
                    del rooms[topic]
        writer.close()
        if joined:
            connected -= 1
            log.info("- %s (%d connected)", conn.peer, connected)


async def main() -> None:
    ap = argparse.ArgumentParser(description="TSDraw relay")
    ap.add_argument("--host", default=None, help="address to listen on (default: all)")
    ap.add_argument("--port", type=int, default=9989)
    ap.add_argument("--password", default="", help="optional shared password (set relayPassword in each TSDraw config)")
    args = ap.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s", datefmt="%H:%M:%S")
    password = args.password.encode()

    server = await asyncio.start_server(lambda r, w: handle(r, w, password), args.host, args.port)
    log.info("TSDraw relay listening on TCP port %d%s", args.port, " (password required)" if password else "")
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
