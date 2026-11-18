import argparse
import asyncio
import json
import time
import uuid

import websockets

# Short load test script for the WebSocket server
# Connects multiple clients to the server and optionally sends simulated audio for testing purposes

async def run_client(index, url, token, seconds, send_audio, end):
    started = time.monotonic()
    # Every simulated client gets its own session key, as the mobile app does
    session_key = f"load-{uuid.uuid4().hex[:24]}"
    try:
        async with websockets.connect(url, open_timeout=10) as ws:
            await ws.send(json.dumps({
                "type": "hello",
                "client": f"load-{index}",
                "token": token,
                "sessionKey": session_key,
            }))
            ack = json.loads(await asyncio.wait_for(ws.recv(), timeout=5))
            if ack.get("type") != "session":
                raise RuntimeError(f"unexpected handshake: {ack}")
            if ack.get("resumed"):
                raise RuntimeError("a fresh key was resumed")

            await ws.send(json.dumps({"type": "audio_config", "sampleRate": 16000, "channels": 1, "encoding": "mulaw"}))

            if send_audio:
                silence = b"\xff" * 1600
                for _ in range(10):
                    await ws.send(silence)
                    await asyncio.sleep(0.1)
                await ws.send(json.dumps({"type": "audio_end"}))

            await asyncio.sleep(seconds)
            if end:
                await ws.send(json.dumps({"type": "session_end"}))
        return f"client {index}: ok ({time.monotonic() - started:.1f}s, session {session_key[:8]})"
    except Exception as e:
        return f"client {index}: FAILED {type(e).__name__}: {e}"


async def main():
    parser = argparse.ArgumentParser()
    # Preset arguments are for localhost, change them to whatever you need
    parser.add_argument("--url", default="ws://127.0.0.1:8765")
    parser.add_argument("--token", default="local-test-token")
    parser.add_argument("--clients", type=int, default=3)
    parser.add_argument("--seconds", type=float, default=10)
    parser.add_argument("--audio", action="store_true")
    parser.add_argument("--end", action="store_true", help="send session_end before closing")
    args = parser.parse_args()

    results = await asyncio.gather(*(
        run_client(i, args.url, args.token, args.seconds, args.audio, args.end)
        for i in range(args.clients)
    ), return_exceptions=True)

    for result in results:
        print(result)

    failures = sum(1 for r in results if isinstance(r, str) and "FAILED" in r)
    if failures:
        raise SystemExit(1)


if __name__ == "__main__":
    asyncio.run(main())
