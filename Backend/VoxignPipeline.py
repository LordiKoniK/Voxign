import asyncio
import base64
import gzip
import hmac
import json
import os
import socket
import struct
from concurrent.futures import ThreadPoolExecutor

from websockets.asyncio.server import broadcast, serve
from websockets.exceptions import ConnectionClosed

from LandmarkExtractor import OUTPUT_PATH, LandmarkLibrary, export_sequence
from SpeechToGloss import RATE, load_translator, microphone_stream, stream_to_deepgram

# Defaults to all interfaces so Android devices can reach the backend directly or through a TLS proxy
# Set VOXIGN_HOST=127.0.0.1 in env to keep it localhost-only during development/testing
HOST = os.getenv("VOXIGN_HOST", "0.0.0.0")
PORT = int(os.getenv("VOXIGN_PORT", "8765"))

# DEV_MODE keeps the original single-machine setup, where the backend records from its own microphone
# Set VOXIGN_DEV_MODE=0/false in env to accept microphone audio from Unity clients instead
DEV_MODE = os.getenv("VOXIGN_DEV_MODE", "1").lower() not in ("0", "false", "no")

# When set, clients must send this token in their hello message
AUTH_TOKEN = os.getenv("VOXIGN_AUTH_TOKEN", "")

# Audio backlog per client before old chunks are dropped (keeps latency low on slow links)
MAX_AUDIO_QUEUE = 64

# Get LAN IP address of the current machine to print it for DEV_MODE users
def lan_addresses():
    addresses = set()
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            addresses.add(info[4][0])
    except OSError:
        pass
    return sorted(address for address in addresses if not address.startswith("127."))

# Lookup table for µ-law audio decoding
def _build_mulaw_table():
    table = []
    for value in range(256):
        u = ~value & 0xFF
        t = ((u & 0x0F) << 3) + 0x84
        t <<= (u & 0x70) >> 4
        table.append(0x84 - t if (u & 0x80) else t - 0x84)
    return tuple(table)

MULAW_TABLE = _build_mulaw_table()

def decode_mulaw(data):
    return struct.pack("<%dh" % len(data), *(MULAW_TABLE[byte] for byte in data))


# Holds the audio -> Deepgram pipeline for a connected Unity client
class ClientAudioSession:

    def __init__(self, pipeline, max_queue=MAX_AUDIO_QUEUE):
        self.pipeline = pipeline
        self.queue = asyncio.Queue(maxsize=max_queue)
        self.task = None
        self.sample_rate = RATE
        self.audio_encoding = "pcm16"
        self.dropped = 0

    # Control messages are JSON text frames (audio_config / audio_end), audio is raw binary
    def handle_text(self, message):
        try:
            data = json.loads(message)
        except json.JSONDecodeError:
            return

        kind = data.get("type")
        if kind == "audio_config":
            try:
                rate = int(data.get("sampleRate", RATE))
            except (TypeError, ValueError):
                rate = RATE
            if rate > 0:
                self.sample_rate = rate
            self.audio_encoding = str(data.get("encoding", "pcm16")).lower()
            print(f"[Unity] Audio stream: {rate} Hz, {data.get('channels', 1)} channel(s), encoding={self.audio_encoding}.")
        elif kind == "audio_end":
            self._finish()

    def feed(self, chunk):
        if not chunk:
            return

        if self.audio_encoding == "mulaw":
            try:
                chunk = decode_mulaw(chunk)
            except Exception as e:
                print(f"[Unity] Could not decode mu-law audio: {e}")
                return

        if self.task is None:
            self.task = asyncio.create_task(self._run())
            print("[Unity] Audio received; streaming to Deepgram.")

        if self.queue.full():
            # Drop the oldest chunk if the queue is full so playback stays in real time instead of growing latency
            try:
                self.queue.get_nowait()
            except asyncio.QueueEmpty:
                pass
            self.dropped += 1
            if self.dropped % 100 == 1:
                print(f"[Unity] Audio backlog full; dropped {self.dropped} chunk(s).")

        try:
            self.queue.put_nowait(chunk)
        except asyncio.QueueFull:
            pass

    # Signals the Deepgram stream to flush and close
    def _finish(self):
        if self.task is not None:
            try:
                self.queue.put_nowait(None)
            except asyncio.QueueFull:
                pass

    async def _run(self):
        try:
            await stream_to_deepgram(self._chunks(), self.pipeline.on_transcript, self.sample_rate)
        except asyncio.CancelledError:
            raise
        except Exception as e:
            print(f"[Unity] Audio stream error: {e}")
        finally:
            self.task = None
            self._drain()

    async def _chunks(self):
        while True:
            chunk = await self.queue.get()
            if chunk is None:
                return
            yield chunk

    def _drain(self):
        while True:
            try:
                self.queue.get_nowait()
            except asyncio.QueueEmpty:
                return

    async def close(self):
        self._finish()
        task = self.task
        if task is not None:
            try:
                await asyncio.wait_for(task, timeout=5)
            except asyncio.TimeoutError:
                task.cancel()
            except asyncio.CancelledError:
                pass
            except Exception as e:
                print(f"[Unity] Audio session error: {e}")
        self.task = None
        self._drain()


# WebSocket server that receives audio from Unity and broadcasts finished sequences back to every client
class UnityBridge:

    def __init__(self, pipeline):
        self.pipeline = pipeline
        self.clients = set()

    # Manages WebSocket connections, authentication, audio streams and disconnections
    async def handler(self, connection):
        session = ClientAudioSession(self.pipeline)
        self.clients.add(connection)
        authorized = not AUTH_TOKEN
        print(f"[Unity] Connected ({len(self.clients)} client(s)).")
        try:
            async for message in connection:
                if isinstance(message, (bytes, bytearray)):
                    if not authorized:
                        await connection.close(code=1008, reason="unauthorized")
                        break
                    session.feed(bytes(message))
                    continue

                try:
                    data = json.loads(message)
                except json.JSONDecodeError:
                    continue

                if data.get("type") == "hello":
                    token = str(data.get("token", ""))
                    if AUTH_TOKEN and not hmac.compare_digest(token.encode(), AUTH_TOKEN.encode()):
                        print("[Unity] Rejected client: invalid token.")
                        await connection.close(code=1008, reason="unauthorized")
                        break
                    authorized = True
                    if AUTH_TOKEN:
                        print("[Unity] Client authenticated.")
                    else:
                        print(f"[Unity] Hello from {data.get('client', 'unknown client')}.")
                elif authorized:
                    session.handle_text(message)
        except ConnectionClosed:
            pass
        finally:
            await session.close()
            self.clients.discard(connection)
            print(f"[Unity] Disconnected ({len(self.clients)} client(s)).")

    # Sends a finished sequence to every client
    # The landmark payload is gzipped to keep transmission costs low, while the JSON file is still written 
    # to the backend's local file system for debugging/DEV_MODE clients
    def notify(self, message):
        if not self.clients:
            print("[Unity] No client connected; sequence saved but not sent.")
            return
        broadcast(self.clients, json.dumps(message))


# Bringing the whole pipeline together (Speech -> English transcript -> ASL gloss -> WLASL landmark sequence -> JSON -> Unity)
class SpeechToSignPipeline:

    def __init__(self, output_path=OUTPUT_PATH):
        self.output_path = output_path
        # Load everything up front, since Deepgram closes idle connections after a few seconds
        self.translator = load_translator()
        self.library = LandmarkLibrary()
        self.bridge = UnityBridge(self)
        # Single worker to keep utterances in order and off the audio thread pool
        self.worker = ThreadPoolExecutor(max_workers=1)

    # Takes transcript from Deepgram, translate it into ASL gloss via T5, and build the landmark sequence JSON file
    def process_transcript(self, transcript):
        gloss = self.translator.translate(transcript)
        words = self.library.resolve_gloss(gloss)
        sequence = self.library.build_sequence(words)
        if sequence:
            export_sequence(sequence, self.output_path)
        return gloss, words, sequence

    # Constant loop to listen for transcripts, process them and send the update to Unity
    async def on_transcript(self, transcript):
        loop = asyncio.get_running_loop()
        gloss, words, sequence = await loop.run_in_executor(self.worker, self.process_transcript, transcript)

        print(f"English:   {transcript}")
        print(f"ASL Gloss: {gloss}")
        print(f"Signs:     {sequence['glossSequence'] if sequence else '(none found)'}\n")

        if sequence:
            raw_payload = json.dumps(sequence).encode("utf-8")
            payload = base64.b64encode(gzip.compress(raw_payload, 6)).decode("ascii")
            self.bridge.notify({
                "type": "sequence",
                "english": transcript,
                "gloss": gloss,
                "glossSequence": sequence["glossSequence"],
                "payload": payload,
                "payloadEncoding": "gzip+base64",
            })

    async def run(self):
        async with serve(self.bridge.handler, HOST, PORT):
            print(f"Unity bridge listening on ws://{HOST}:{PORT}")
            for address in lan_addresses():
                print(f"  For devices on the LAN: ws://{address}:{PORT}")
            print("Authentication: " + ("required" if AUTH_TOKEN else "disabled (set VOXIGN_AUTH_TOKEN to require a token)."))

            if DEV_MODE:
                print("DEV_MODE: using the backend's own microphone.")
                await microphone_stream(self.on_transcript)
            else:
                print("DEV_MODE is off: waiting for microphone audio from Unity clients...")
                await asyncio.Future()


def main():
    try:
        pipeline = SpeechToSignPipeline()
    except FileNotFoundError as e:
        print(f"Error: {e}")
        return

    try:
        asyncio.run(pipeline.run())
    except KeyboardInterrupt:
        print("\nPipeline stopped cleanly.")
    finally:
        pipeline.worker.shutdown(wait=False)


if __name__ == "__main__":
    main()
