import asyncio
import base64
import csv
import gzip
import hmac
import json
import os
import re
import socket
import struct
import threading
import time
import uuid
from concurrent.futures import ThreadPoolExecutor

from websockets.asyncio.server import serve
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

# How long a disconnected session is retained so a client can reconnect with the same key
SESSION_TTL = float(os.getenv("VOXIGN_SESSION_TTL", "300"))
# How often expired sessions are swept
SESSION_SWEEP = float(os.getenv("VOXIGN_SESSION_SWEEP", "30"))
# Upper bound on retained sessions, a guard against unauthenticated key churn
MAX_SESSIONS = int(os.getenv("VOXIGN_MAX_SESSIONS", "64"))

# T5 is CPU-bound; on the 2 vCPU deployment a single worker serialises inference while
# Deepgram streams run in parallel. Raise this only on a machine with spare cores.
T5_WORKERS = max(1, int(os.getenv("VOXIGN_T5_WORKERS", "1")))

# Session keys are client-generated identifiers, not credentials, but bound their shape
SESSION_KEY_PATTERN = re.compile(r"^[A-Za-z0-9_-]{8,64}$")

# Test instrumentation. Only active in DEV_MODE unless VOXIGN_METRICS forces it on,
# so a production deployment never writes measurement files
METRICS = os.getenv("VOXIGN_METRICS", "1" if DEV_MODE else "0").lower() not in ("0", "false", "no")

# Per-utterance latency rows and the raw transcripts used for word-error evaluation
METRICS_DIR = os.getenv("VOXIGN_METRICS_DIR", os.path.join(os.path.dirname(os.path.abspath(__file__)), "Logs"))
LATENCY_CSV = os.path.join(METRICS_DIR, "latency.csv")
TRANSCRIPT_LOG = os.path.join(METRICS_DIR, "transcripts.jsonl")

LATENCY_FIELDS = [
    "timestamp",
    "session",
    "utterance",
    "english",
    "gloss",
    "signs",
    "asr_ms",
    "translate_ms",
    "lookup_ms",
    "encode_ms",
    "broadcast_ms",
    "pipeline_ms",
    "total_ms",
]

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


# Holds the audio -> Deepgram pipeline for a single WebSocket connection.
# A session can outlive its connection; a new connection creates a fresh audio stream.
class ClientAudioSession:

    def __init__(self, pipeline, session, max_queue=MAX_AUDIO_QUEUE):
        self.pipeline = pipeline
        self.session = session
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
            print(f"[Unity] Audio stream ({self.session.short}): {rate} Hz, "
                  f"{data.get('channels', 1)} channel(s), encoding={self.audio_encoding}.")
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
            print(f"[Unity] Audio received from {self.session.short}; streaming to Deepgram.")

        if self.queue.full():
            # Drop the oldest chunk if the queue is full so playback stays in real time instead of growing latency
            try:
                self.queue.get_nowait()
            except asyncio.QueueEmpty:
                pass
            self.dropped += 1
            if self.dropped % 100 == 1:
                print(f"[Unity] {self.session.short}: audio backlog full; dropped {self.dropped} chunk(s).")

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

    async def _deliver(self, transcript, trailing=None):
        # A stream that was replaced by a reconnect must not deliver into the new connection
        if self.session.audio is not self:
            return
        await self.pipeline.on_transcript(self.session, transcript, trailing)

    async def _run(self):
        try:
            await stream_to_deepgram(self._chunks(), self._deliver, self.sample_rate)
        except asyncio.CancelledError:
            raise
        except Exception as e:
            print(f"[Unity] Audio stream error ({self.session.short}): {e}")
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
                print(f"[Unity] Audio session error ({self.session.short}): {e}")
        self.task = None
        self._drain()


# A logical conversation identified by the client-generated session key.
# The connection can come and go; the key keeps the session addressable for the grace period.
class ClientSession:

    def __init__(self, key, pipeline):
        self.key = key
        self.short = key[:8]
        self.pipeline = pipeline
        self.connection = None
        self.audio = None
        self.last_activity = time.monotonic()
        self.utterance = 0

    def touch(self):
        self.last_activity = time.monotonic()

    def feed(self, chunk):
        if self.audio is None:
            self.audio = ClientAudioSession(self.pipeline, self)
        self.audio.feed(chunk)

    def handle_text(self, message):
        # audio_config normally arrives before the first audio frame, so the stream is created here
        if self.audio is None:
            self.audio = ClientAudioSession(self.pipeline, self)
        self.audio.handle_text(message)

    async def close_audio(self):
        audio = self.audio
        self.audio = None
        if audio is not None:
            await audio.close()


# WebSocket server that receives audio from Unity and sends finished sequences back to their session
class UnityBridge:

    def __init__(self, pipeline):
        self.pipeline = pipeline
        self.sessions = {}
        self.connections = set()
        # Synthetic session used when the backend records its own microphone
        self.dev_session = ClientSession("dev", pipeline)

    # Manages WebSocket connections, authentication, audio streams and disconnections
    async def handler(self, connection):
        session = None
        authorized = not AUTH_TOKEN
        self.connections.add(connection)
        print(f"[Unity] Connected ({len(self.connections)} connection(s), {len(self.sessions)} session(s)).")
        try:
            async for message in connection:
                if isinstance(message, (bytes, bytearray)):
                    if not authorized:
                        await connection.close(code=1008, reason="unauthorized")
                        break
                    if session is None or session.connection is not connection:
                        continue
                    session.touch()
                    session.feed(bytes(message))
                    continue

                try:
                    data = json.loads(message)
                except json.JSONDecodeError:
                    continue

                kind = data.get("type")

                if kind == "hello":
                    # One handshake per connection; later hellos are ignored
                    if session is not None:
                        continue

                    token = str(data.get("token", ""))
                    if AUTH_TOKEN and not hmac.compare_digest(token.encode(), AUTH_TOKEN.encode()):
                        print("[Unity] Rejected client: invalid token.")
                        await connection.close(code=1008, reason="unauthorized")
                        break

                    authorized = True
                    key = data.get("sessionKey")
                    if not isinstance(key, str) or not key:
                        # Older clients do not send a key; mint one so the connection still works
                        key = uuid.uuid4().hex
                        print(f"[Unity] Hello without a session key; assigned {key[:8]}.")
                    elif not SESSION_KEY_PATTERN.match(key):
                        print("[Unity] Rejected client: invalid session key.")
                        await connection.close(code=1008, reason="invalid session key")
                        break

                    try:
                        session, resumed = self.attach(key, connection)
                    except RuntimeError:
                        print("[Unity] Rejected client: session limit reached.")
                        await connection.close(code=1013, reason="server busy")
                        break

                    if AUTH_TOKEN:
                        print(f"[Unity] Client authenticated (session {session.short}).")
                    else:
                        print(f"[Unity] Hello from {data.get('client', 'unknown client')} (session {session.short}).")
                    if resumed:
                        print(f"[Unity] Resumed session {session.short}.")
                    await connection.send(json.dumps({
                        "type": "session",
                        "sessionKey": session.key,
                        "resumed": resumed,
                        "ttlSeconds": int(SESSION_TTL),
                    }))
                    continue

                if session is None:
                    continue
                # A stale socket that was replaced by a reconnect must not act on the new session
                if session.connection is not connection:
                    continue
                session.touch()

                if kind == "session_end":
                    await self.end(session)
                    print(f"[Unity] Session {session.short} ended by the client.")
                    session = None
                    await connection.close(code=1000, reason="session ended")
                    break

                session.handle_text(message)
        except ConnectionClosed:
            pass
        finally:
            if session is not None:
                await self.detach(session, connection)
            self.connections.discard(connection)
            print(f"[Unity] Disconnected ({len(self.connections)} connection(s), {len(self.sessions)} session(s)).")

    # Returns the existing session for a key (resuming it) or creates a new one
    def attach(self, key, connection):
        session = self.sessions.get(key)
        resumed = session is not None
        if session is None:
            if len(self.sessions) >= MAX_SESSIONS:
                raise RuntimeError("session limit reached")
            session = ClientSession(key, self.pipeline)
            self.sessions[key] = session

        previous = session.connection
        session.connection = connection
        session.touch()

        # A new connection starts a fresh audio stream; the old one belonged to the previous socket
        previous_audio = session.audio
        session.audio = None
        if previous_audio is not None:
            asyncio.create_task(previous_audio.close())

        # A reconnect can race an old socket that has not yet closed; retire the old one
        if previous is not None and previous is not connection:
            asyncio.create_task(self._close_quietly(previous, 4001, "session resumed elsewhere"))

        return session, resumed

    # Detaches a connection but keeps the session for the grace period
    async def detach(self, session, connection):
        if session.connection is connection:
            session.connection = None
            session.touch()
            await session.close_audio()

    # Ends a session immediately (client asked, or shutdown)
    async def end(self, session):
        self.sessions.pop(session.key, None)
        session.connection = None
        await session.close_audio()

    # Periodically discards sessions that have been disconnected for longer than the grace period
    async def sweep(self):
        while True:
            await asyncio.sleep(SESSION_SWEEP)
            await self.expire()

    # Single expiry pass, kept separate so it can be exercised directly
    async def expire(self):
        now = time.monotonic()
        for key, session in list(self.sessions.items()):
            if session.connection is None and (now - session.last_activity) > SESSION_TTL:
                await session.close_audio()
                self.sessions.pop(key, None)
                print(f"[Unity] Session {session.short} expired after {SESSION_TTL:.0f}s idle.")

    # Sends a finished sequence to the session that produced it
    # The landmark payload is gzipped to keep transmission costs low, while the JSON file is still written
    # to the backend's local file system in DEV_MODE for debugging
    async def notify(self, session, message):
        connection = session.connection
        if connection is None:
            print(f"[Unity] Session {session.short} is not connected; sequence saved but not sent.")
            return False
        try:
            await connection.send(json.dumps(message))
            return True
        except ConnectionClosed:
            print(f"[Unity] Session {session.short} disconnected before the sequence was sent.")
            return False

    @staticmethod
    async def _close_quietly(connection, code, reason):
        try:
            await connection.close(code=code, reason=reason)
        except Exception:
            pass


# Bringing the whole pipeline together (Speech -> English transcript -> ASL gloss -> WLASL landmark sequence -> JSON -> Unity)
class SpeechToSignPipeline:

    def __init__(self, output_path=OUTPUT_PATH):
        self.output_path = output_path
        # Load everything up front, since Deepgram closes idle connections after a few seconds
        self.translator = load_translator()
        self.library = LandmarkLibrary()
        self.bridge = UnityBridge(self)
        # A single worker serialises T5 inference; each session's transcripts already arrive in order
        self.worker = ThreadPoolExecutor(max_workers=T5_WORKERS)
        # The landmark archive keeps an open NPZ handle, so lookups are serialised when several
        # workers are configured. With the default single worker this is uncontended.
        self.library_lock = threading.Lock()
        if METRICS:
            os.makedirs(METRICS_DIR, exist_ok=True)
            self._rotate_metrics_if_needed()
            print(f"Metrics: writing {LATENCY_CSV} and {TRANSCRIPT_LOG}.")

    # Keeps the CSV readable when the columns change between releases
    def _rotate_metrics_if_needed(self):
        if not os.path.exists(LATENCY_CSV):
            return
        try:
            with open(LATENCY_CSV, newline="", encoding="utf-8") as f:
                header = next(csv.reader(f), None)
        except OSError:
            return
        if header != LATENCY_FIELDS:
            backup = LATENCY_CSV.replace(".csv", "_" + time.strftime("%Y%m%d-%H%M%S") + ".csv")
            os.replace(LATENCY_CSV, backup)
            print(f"Metrics: previous latency.csv had an older schema; moved to {backup}.")

    # Takes transcript from Deepgram, translate it into ASL gloss via T5, and build the landmark sequence JSON file
    # Stage timings are filled into "timings" when measurement is enabled
    def process_transcript(self, transcript, timings=None):
        t_start = time.perf_counter()
        gloss = self.translator.translate(transcript)
        t_translate = time.perf_counter()
        with self.library_lock:
            words = self.library.resolve_gloss(gloss)
            sequence = self.library.build_sequence(words)
        t_lookup = time.perf_counter()
        # The debug file is shared by all clients, so it is only written for the single-machine mode
        if sequence and DEV_MODE:
            export_sequence(sequence, self.output_path)

        if timings is not None:
            timings["translate_ms"] = (t_translate - t_start) * 1000.0
            timings["lookup_ms"] = (t_lookup - t_translate) * 1000.0
            timings["signs"] = len(sequence["glossSequence"]) if sequence else 0
        return gloss, words, sequence

    # Handles one final transcript for one session and returns the sequence to that session only.
    # trailing is the audio processed after the last word, which estimates how long the user waited
    async def on_transcript(self, session, transcript, trailing=None):
        t_transcript = time.perf_counter()
        session.utterance += 1
        utterance = session.utterance
        timings = {} if METRICS else None
        loop = asyncio.get_running_loop()
        gloss, words, sequence = await loop.run_in_executor(self.worker, self.process_transcript, transcript, timings)
        t_processed = time.perf_counter()

        print(f"[{session.short}] English:   {transcript}")
        print(f"[{session.short}] ASL Gloss: {gloss}")
        print(f"[{session.short}] Signs:     {sequence['glossSequence'] if sequence else '(none found)'}\n")

        if sequence:
            raw_payload = json.dumps(sequence).encode("utf-8")
            payload = base64.b64encode(gzip.compress(raw_payload, 6)).decode("ascii")
            t_encoded = time.perf_counter()
            await self.bridge.notify(session, {
                "type": "sequence",
                "sessionKey": session.key,
                "utterance": utterance,
                "english": transcript,
                "gloss": gloss,
                "glossSequence": sequence["glossSequence"],
                "payload": payload,
                "payloadEncoding": "gzip+base64",
            })
            t_broadcast = time.perf_counter()
            if METRICS:
                self._record_metrics(session, utterance, transcript, gloss, timings, trailing,
                                     t_transcript, t_processed, t_encoded, t_broadcast)
        elif METRICS:
            self._record_metrics(session, utterance, transcript, gloss, timings, trailing,
                                 t_transcript, t_processed, t_processed, t_processed)

    # Writes one latency row per utterance and appends the raw pair used by the word-error evaluation
    def _record_metrics(self, session, utterance, transcript, gloss, timings, trailing,
                        t_transcript, t_processed, t_encoded, t_broadcast):
        pipeline_ms = (t_broadcast - t_transcript) * 1000.0
        asr_ms = trailing * 1000.0 if trailing is not None else None
        row = {
            "timestamp": time.strftime("%Y-%m-%dT%H:%M:%S"),
            "session": session.short,
            "utterance": utterance,
            "english": transcript,
            "gloss": gloss,
            "signs": timings.get("signs", 0),
            "asr_ms": "" if asr_ms is None else round(asr_ms, 1),
            "translate_ms": round(timings.get("translate_ms", 0.0), 1),
            "lookup_ms": round(timings.get("lookup_ms", 0.0), 1),
            "encode_ms": round((t_encoded - t_processed) * 1000.0, 1),
            "broadcast_ms": round((t_broadcast - t_encoded) * 1000.0, 1),
            "pipeline_ms": round(pipeline_ms, 1),
            "total_ms": "" if asr_ms is None else round(asr_ms + pipeline_ms, 1),
        }

        new_file = not os.path.exists(LATENCY_CSV)
        try:
            with open(LATENCY_CSV, "a", newline="", encoding="utf-8") as f:
                writer = csv.DictWriter(f, fieldnames=LATENCY_FIELDS)
                if new_file:
                    writer.writeheader()
                writer.writerow(row)
            with open(TRANSCRIPT_LOG, "a", encoding="utf-8") as f:
                f.write(json.dumps({"session": session.short, "english": transcript, "gloss": gloss}) + "\n")
        except OSError as e:
            print(f"Metrics error: {e}")
        print(f"[{session.short}] Latency: total {row['total_ms']} ms (ASR {row['asr_ms']}, "
              f"translate {row['translate_ms']}, lookup {row['lookup_ms']}, "
              f"encode {row['encode_ms']}, broadcast {row['broadcast_ms']}).")

    async def on_dev_transcript(self, transcript, trailing=None):
        await self.on_transcript(self.bridge.dev_session, transcript, trailing)

    async def run(self):
        sweeper = asyncio.create_task(self.bridge.sweep())
        try:
            async with serve(self.bridge.handler, HOST, PORT):
                print(f"Unity bridge listening on ws://{HOST}:{PORT}")
                for address in lan_addresses():
                    print(f"  For devices on the LAN: ws://{address}:{PORT}")
                print("Authentication: " + ("required" if AUTH_TOKEN else "disabled (set VOXIGN_AUTH_TOKEN to require a token)."))
                print(f"Sessions: {SESSION_TTL:.0f}s grace after disconnect; T5 workers: {T5_WORKERS}.")

                if DEV_MODE:
                    print("DEV_MODE: using the backend's own microphone.")
                    await microphone_stream(self.on_dev_transcript)
                else:
                    print("DEV_MODE is off: waiting for microphone audio from Unity clients...")
                    await asyncio.Future()
        finally:
            sweeper.cancel()


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
