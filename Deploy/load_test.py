import argparse
import asyncio
import json
import time

import websockets

# Short load test script for the WebSocket server
# Connects multiple clients to the server and optionally sends simulated audio for testing purposes

async def run_client(index, url, token, seconds, send_audio):
    started = time.monotonic()
    try:
        async with websockets.connect(url, open_timeout=10) as ws:
            await ws.send(json.dumps({"type": "hello", "client": f"load-{index}", "token": token}))
            await ws.send(json.dumps({"type": "audio_config", "sampleRate": 16000, "channels": 1, "encoding": "mulaw"}))

            if send_audio:
                silence = b"\xff" * 1600
                for _ in range(10):
                    await ws.send(silence)
                    await asyncio.sleep(0.1)
                await ws.send(json.dumps({"type": "audio_end"}))

            await asyncio.sleep(seconds)
        return f"client {index}: ok ({time.monotonic() - started:.1f}s)"
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
    args = parser.parse_args()

    results = await asyncio.gather(*(
        run_client(i, args.url, args.token, args.seconds, args.audio)
        for i in range(args.clients)
    ), return_exceptions=True)

    for result in results:
        print(result)

    failures = sum(1 for r in results if isinstance(r, str) and "FAILED" in r)
    if failures:
        raise SystemExit(1)


if __name__ == "__main__":
    asyncio.run(main())
