# Scripts and Architecture

This document describes every backend and Unity script, the WebSocket protocol, and how the pieces fit together. Setup instructions are in the [README](../README.md).

## End-to-end flow

```mermaid
sequenceDiagram
    participant U as Unity client
    participant R as SequenceReceiver
    participant B as VoxignPipeline
    participant D as Deepgram
    participant T as T5 + WLASL

    U->>R: microphone frames
    R->>B: wss:// … hello {token, sessionKey}
    B-->>R: session {sessionKey, resumed, ttlSeconds}
    R->>B: audio_config {sampleRate, encoding}
    R->>B: binary μ-law frames
    B->>D: PCM16 stream
    D-->>B: final transcript
    B->>T: gloss translation + landmark lookup
    T-->>B: landmark sequence
    B-->>R: sequence {sessionKey, utterance, payload gzip+base64}
    R->>U: decode, queue signs, update panels
```

- **Client streaming mode** (`VOXIGN_DEV_MODE=0`): Unity captures the microphone and streams it.
- **Backend microphone mode** (`VOXIGN_DEV_MODE=1`, default): the backend records locally; Unity still connects to receive sequences.
- **Sessions:** the client generates a key at launch and sends it in `hello`. The server maps the key to a `ClientSession`, so several devices can be served at once and a reconnecting device resumes the same conversation. A disconnected session is kept for `VOXIGN_SESSION_TTL` seconds (default 300).

## Backend (`Backend/`)

### `VoxignPipeline.py` — entry point and WebSocket bridge

Starts the `websockets` server on `VOXIGN_HOST:VOXIGN_PORT` (default `0.0.0.0:8765`) and owns the whole pipeline.

- **`UnityBridge`**
  - Accepts connections and maps each to a `ClientSession` by the client-supplied `sessionKey`.
  - Validates the `hello` message: when `VOXIGN_AUTH_TOKEN` is set, the client token must match (constant-time comparison). Invalid tokens are closed with status **1008**; malformed keys also close with **1008**; a full session table closes with **1013**.
  - Sends a `session` acknowledgement after the handshake, including `resumed` and `ttlSeconds`.
  - Reuses an existing session when the same key reconnects, retiring any older socket with status **4001**.
  - Routes binary frames to that session's `ClientAudioSession`; forwards text control messages (`audio_config`, `audio_end`).
  - Sends each completed sequence to the session that produced it, never to other clients.
  - `session_end` ends the session immediately; otherwise `expire` discards disconnected sessions after the grace period.
- **`ClientSession`** (one per key, may span several connections)
  - Holds the current socket, the audio stream, the last-activity time and the per-session utterance counter.
  - Created on the first `hello`, reused on reconnect, kept for `VOXIGN_SESSION_TTL` after a disconnect.
- **`ClientAudioSession`** (one per connection)
  - Holds an `asyncio.Queue` (max 64 chunks; oldest is dropped under backpressure).
  - Reads `audio_config` for the sample rate and encoding (`pcm16` or `mulaw`).
  - Decodes μ-law to PCM16 before forwarding to Deepgram.
  - Lazily starts a Deepgram task on the first audio chunk; `audio_end` flushes and closes the Deepgram stream.
  - Cancels/cleans up on disconnect or session end.
- **`SpeechToSignPipeline`**
  - Loads the T5 translator and WLASL library once at startup.
  - `process_transcript`: T5 → gloss, gloss → WLASL words, words → landmark sequence, and writes the debug JSON in DEV_MODE.
  - Runs T5 inference on `VOXIGN_T5_WORKERS` threads (default 1). Each session's final transcripts arrive in order and are awaited in order, so multiple clients are served by organised succession; inference is CPU-bound and one worker is the right default on 2 vCPUs.
  - `on_transcript`: logs English/gloss/signs and sends a sequence message (with `sessionKey` and a per-session `utterance` number) to that session only.
  - `run`: serves the bridge and sweeps expired sessions; in DEV_MODE awaits `microphone_stream`, otherwise waits for client audio.
  - `_record_metrics` appends one row per utterance to `Backend/Logs/latency.csv` (including `session` and `utterance`) and the transcript pair to `Backend/Logs/transcripts.jsonl`. Measurement is enabled by default only when `VOXIGN_DEV_MODE` is on.
  - `asr_ms` is derived from the Deepgram word timings: the audio processed after the final word, i.e. the endpointing wait the speaker experienced.
- **Environment**
  - `VOXIGN_HOST` (default `0.0.0.0`), `VOXIGN_PORT` (8765)
  - `VOXIGN_DEV_MODE` (1 = backend mic, 0 = client audio)
  - `VOXIGN_AUTH_TOKEN` (empty = auth disabled)
  - `VOXIGN_SEQUENCE_PATH` (debug JSON path; container default `/app/test_sequence.json`)
  - `VOXIGN_METRICS` (default: on in DEV_MODE, off otherwise) and `VOXIGN_METRICS_DIR` (default `Backend/Logs`)
  - `VOXIGN_SESSION_TTL` (seconds a disconnected session is retained, default 300), `VOXIGN_SESSION_SWEEP` (seconds between expiry passes, default 30)
  - `VOXIGN_MAX_SESSIONS` (default 64) and `VOXIGN_T5_WORKERS` (default 1; keep at 1 on 2 vCPUs)

### `SpeechToGloss.py` — Deepgram + T5

- **`GlossTranslator`** loads `Models/t5_asl_gloss/` (config, tokenizer, `model.safetensors`) on CPU or CUDA and translates each sentence with beam search (`gloss_config.json`).
- **`stream_to_deepgram(chunks, on_transcript, sample_rate)`** opens the Deepgram streaming WebSocket (Nova-3, `linear16`, `smart_format`) and runs a sender/receiver pair. When the chunk iterator ends, it sends `CloseStream` so Deepgram flushes the final transcript.
- **`microphone_stream(on_transcript)`** is the development path: opens PyAudio (lazily imported, so containers do not need PortAudio), reads 1024-sample blocks, and forwards them to Deepgram.
- **`load_translator()`** validates the model directory and raises `FileNotFoundError` if it is missing.
- The model is `google-t5/t5-small` fine-tuned over 29 epochs (242 MB `model.safetensors`); `gloss_config.json` reports BLEU 74.45, chrF 92.1, and exact-match 79.53.
- `python SpeechToGloss.py` runs a standalone transcript → gloss test.

### `LandmarkExtractor.py` — WLASL library

- Paths: `DATA_DIR = Backend/Data` (`WLASL_parsed_data.json`, `landmarks_V3.npz`); `OUTPUT_PATH = VOXIGN_SEQUENCE_PATH` or `Assets/test_sequence.json`.
- **`LandmarkLibrary`**
  - Loads the parsed JSON and opens the NPZ archive lazily (individual sign arrays are read on demand, so the 8.9 GB archive is not loaded into RAM).
  - Indexes entries by gloss and video ID; selects the entry with the most hand-tracked frames when a gloss has several candidates.
  - `resolve_gloss` prefers multi-word phrases, converts digits to number words, and falls back to fingerspelling (skipping letters with no sign).
  - `build_series` trims idle frames, maps landmarks to points, and attaches gloss/frame metadata.
  - `build_sequence` assembles the final JSON structure.
- `export_sequence` writes via a temporary file and atomic rename.
- Standalone options at the top (`target_sequence`, `target_video_id`, `target_gloss`) allow `python LandmarkExtractor.py` for offline tests.

### `SpeechRecognizer.py`

A small standalone script that streams the microphone to Deepgram and prints raw transcripts. Useful for verifying the key, network, and microphone without loading T5/WLASL.

### `MediapipeTester.py`

Reads `Assets/test_sequence.json` and renders the landmark frames in [Rerun](https://rerun.io) for offline inspection. The `rerun-sdk` package includes the SDK but not the viewer GUI; place a matching viewer binary at `.venv/Scripts/rerun.exe` (Windows) or `.venv/bin/rerun` (Linux), or install it separately.

### `evaluate_wer.py` — word-error evaluation

Computes the word error rate of the recorded Deepgram transcripts against a reference phrase list, for functional requirement FR-02.

- `python Backend/evaluate_wer.py --references phrases.txt` reads `Backend/Logs/transcripts.jsonl` (produced by a DEV_MODE run) and compares the transcripts with the phrases in spoken order.
- Prints per-utterance substitutions, deletions, insertions and WER, the corpus WER, and a pass/fail against the 10 per cent target; `--json results.json` writes a machine-readable summary.
- The reference file has one phrase per line; blank lines and `#` comments are ignored.

### Dependencies and image

- `requirements.txt` — local development: numpy, rerun-sdk, websockets, pyaudio, python-dotenv, torch, transformers, sentencepiece.
- `requirements-prod.txt` — container: numpy, websockets, python-dotenv, transformers, sentencepiece. **PyTorch is installed separately** in the Dockerfile from the CPU index so the correct CPU/ARM wheel is selected. `pyaudio` is absent because production audio comes from Unity.
- `Dockerfile` — Python 3.12-slim, `libgomp1` for torch, CPU torch, production requirements, app code. Models and data are mounted at runtime.
- `.dockerignore` — excludes `.env`, `Data/`, `Models/`, caches.

## Unity scripts (`Assets/Scripts/`)

### Networking and audio

- **`SequenceReceiver`** (on `Louise`, requires `SignPlayer`)
  - Chooses the endpoint: **Server Url** in the editor; **Production Server Url** on device when **Use Production Url On Device** is enabled.
  - Opens `ClientWebSocket` on a background task, sends `hello` with the auth token and this run's `sessionKey`, and sets a 25 s keepalive.
  - Generates the session key once per app process (`Guid.NewGuid().ToString("N")` into a static field) and never persists it, so every app launch starts a new session while scene reloads keep the same key.
  - Sends `session_end` on `OnApplicationQuit`/`OnDestroy` and gives it a short window to leave before cancelling the connection. If the process is killed without warning, the server's grace period releases the key.
  - Reconnects with exponential backoff (2 s → 30 s, jittered) using the same key, so a network interruption resumes the session instead of starting a new one.
  - A single send pump serializes control messages and binary audio; `SendAudio`/`SendControl` return false while disconnected.
  - Receives fragmented messages, decompresses gzip+base64 sequences, falls back to the exported JSON file for development, and enqueues playback data on the main thread.
  - Raises `onTranscription(string)` and exposes `IsConnected`.
- **`VoiceAudioStreamer`** (on `Louise`, requires `SequenceReceiver`)
  - Requests Android `RECORD_AUDIO` permission when needed.
  - Captures the default microphone at the requested rate, reads 100 ms chunks with wrap handling, and converts to PCM16 or G.711 μ-law.
  - Sends `audio_config` (rate, channels, encoding) and streams frames while connected; sends `audio_end` on stop.
  - `devMode` disables streaming when the backend records its own microphone; it is forced off on mobile.
  - `onStreamingChanged` lets the toggle reflect the real state; permission/device failures invoke it with `false`.
- **`VoiceToggleScript`** (on `VoiceToggle`, requires `Button`)
  - Toggle visuals (knob/colours) with a tween; a Button click flips the state.
  - Finds the `VoiceAudioStreamer` (retries for up to 30 s so it works when `Main` loads additively) and starts/stops capture.
  - Synchronizes the switch if streaming fails or stops externally; exposes `SetValue`, `SetValueWithoutNotify`, `IsOn`.
- **`MainSceneBootstrap`** (on `AppBootstrap` in `VoxignMobileUI`)
  - After one frame, loads `Main` additively if it is not already loaded. The delay lets the editor finish registering its open scenes, preventing duplicate `Main` instances and audio listeners.

### Playback and rig

- **`SignPlayer`** (requires `LandmarkMapper`, `IKSolver`, `AnimationController`)
  - Queues `LandmarkSeries`; plays them in order with a 0.25 s blend between signs.
  - Builds an opening pose, releases unused hands, applies frames at the source frame rate, and raises `SignStarted(string)` at the start of each sign (used by the current-sign UI).
- **`LandmarkMapper`**
  - Converts MediaPipe coordinates (x left, y down, z away) to Unity space, including the source aspect ratio.
  - Maps shoulders/elbows/wrists to upper-arm and forearm directions; maps wrist, hand roll, thumb, and four fingers to bone targets.
- **`IKSolver`**
  - Applies hierarchical bend limits (forearm ≤150°, finger/thumb limits) so poses stay anatomically plausible.
- **`AnimationController`**
  - Caches the arm/hand bone map and rest pose, then each `LateUpdate` resets to rest and aims bones at the current targets.
  - Provides smoothing, transition blending, and hand release; the rig is applied on top of Unity's animation updates.
- **`LandmarkDataTypes`**
  - `LandmarkFrame`, `LandmarkSeries`, `LandmarkSequence`, `BoneTarget`, `BoneRotationSet`.
  - `LandmarkSequence.FromJson` supports both multi-sign and older single-sign exports.
- **`PipelineTester`** (development)
  - Optional component that loads a `TextAsset` (or pasted JSON) and queues its signs on `SignPlayer` at startup, without the backend.

### Measurement (development only)

- **`DevMetrics`** (auto-added to the avatar in the editor and in development builds)
  - Measures **last audio chunk → sequence received** (`speech_to_payload_ms`), **sequence received → first sign rendered** (`payload_to_render_ms`), the average and lowest frame rate during playback, and the outage length on each automatic reconnect.
  - Appends rows to `dev_metrics.csv` in `Application.persistentDataPath` and logs a per-run summary; nothing is attached or compiled into a release build.
  - The audio-to-payload figure is only produced when Unity streams audio (`VoiceAudioStreamer.devMode` off and `VOXIGN_DEV_MODE=0`); the render and frame-rate figures are produced in either mode.

### UI and presentation

- **`TranscriptionUI`** (on `UIRoot`)
  - Binds to `SignPlayer.UtteranceStarted` and `SignPlayer.SignStarted` at runtime (retries while `Main` loads).
  - Writes the English transcript of the utterance being played to the top panel and the current sign (uppercase) to the chip below it, so both follow playback rather than the network.
  - Hides the chip when playback stops.
- **`CameraOrbit`** (on `Main Camera`)
  - Orbits `mixamorig8:Spine1` with a configurable offset, so the Mixamo feet→hips root shift does not move the framing.
  - Desktop: left-drag to orbit, wheel to zoom. Android: one-finger drag, two-finger pinch.
  - Clamps pitch and distance. Mouse drags that start over UI do not rotate the camera; touch drags orbit from anywhere.

### Editor-only

- **`AndroidManifestPermissions`**
  - `IPostGenerateGradleAndroidProject` hook that adds `INTERNET` and `RECORD_AUDIO`.
  - Adds `usesCleartextTraffic` only for **Development** builds; release builds require `wss://`.
- **`PipelineTester`** is runtime but intended for offline data tests (see above).

## Deploy files (`Deploy/`)

- **`docker-compose.yml`** — production: `backend` (models/data mounted, `DEV_MODE=0`, auth token required) and `caddy` (ports 80/443, automatic TLS, `VOXIGN_DOMAIN`).
- **`docker-compose.local.yml`** — local production-like: backend only, published on 8765, fixed `local-test-token`, no Caddy.
- **`Caddyfile`** — `{$VOXIGN_DOMAIN} { reverse_proxy backend:8765 }`; handles the WebSocket upgrade and certificates.
- **`.env.example`** — template for `Deploy/.env` (`VOXIGN_DOMAIN`, `VOXIGN_AUTH_TOKEN`).
- **`load_test.py`** — opens N authenticated WebSocket clients, each with its own generated `sessionKey`, and holds them open; `--audio` sends μ-law silence and opens Deepgram sessions, `--end` sends `session_end` before closing. Use it to verify auth and concurrency; it does not evaluate recognition accuracy.

## WebSocket protocol

All control messages are UTF-8 JSON text frames; audio is binary.

| Direction | Message | Notes |
|---|---|---|
| Client → server | `{"type":"hello","client":"unity","token":"…","sessionKey":"…"}` | Must be first; invalid token or key → close 1008; omitted key is minted by the server |
| Server → client | `{"type":"session","sessionKey":"…","resumed":false,"ttlSeconds":300}` | Handshake acknowledgement |
| Client → server | `{"type":"audio_config","sampleRate":16000,"channels":1,"encoding":"mulaw"}` | `mulaw` or `pcm16` |
| Client → server | binary | μ-law bytes (1 byte/sample) or little-endian PCM16 |
| Client → server | `{"type":"audio_end"}` | Flush the current utterance and close the Deepgram stream |
| Client → server | `{"type":"session_end"}` | End the session and release its key immediately |
| Server → client | `{"type":"sequence","sessionKey":"…","utterance":1,"english":"…","gloss":"…","glossSequence":[...],"payload":"<base64 gzip>","payloadEncoding":"gzip+base64"}` | Sent only to the session that produced it |
| Server → client | close `1008` / `1013` / `4001` | Authentication or key failure / session limit / same key reconnected elsewhere |

The client reconnects automatically with the same key. A disconnected session is retained for `VOXIGN_SESSION_TTL` seconds (default 300) and then discarded; an explicit `session_end` removes it at once. The debug JSON is only written in DEV_MODE because the file is shared by all clients.

## Configuration reference

### Backend environment

| Variable | Default | Purpose |
|---|---|---|
| `VOXIGN_HOST` | `0.0.0.0` | Bind address |
| `VOXIGN_PORT` | `8765` | Listen port |
| `VOXIGN_DEV_MODE` | `1` | `1` = backend microphone; `0` = Unity streams audio |
| `VOXIGN_AUTH_TOKEN` | empty | Shared secret; empty disables auth |
| `VOXIGN_SEQUENCE_PATH` | `Assets/test_sequence.json` | Debug JSON output; container sets `/app/test_sequence.json` |
| `VOXIGN_METRICS` | on when `VOXIGN_DEV_MODE=1` | Writes the latency CSV and transcript log |
| `VOXIGN_METRICS_DIR` | `Backend/Logs` | Output directory for measurement files |
| `VOXIGN_SESSION_TTL` | `300` | Seconds a disconnected session is retained before its key is discarded |
| `VOXIGN_SESSION_SWEEP` | `30` | Seconds between expiry passes |
| `VOXIGN_MAX_SESSIONS` | `64` | Upper bound on retained sessions; excess handshakes close with 1013 |
| `VOXIGN_T5_WORKERS` | `1` | Inference worker threads; keep at 1 on 2 vCPUs |
| `DEEPGRAM_API_KEY` | — | Required; loaded from `Backend/.env` |

### Unity endpoint selection

- Editor: **Server Url** (default `ws://127.0.0.1:8765`).
- Device: **Production Server Url** when **Use Production Url On Device** is on.
- **Auth Token** is sent for both; it must match the backend when auth is enabled.
- `VoiceAudioStreamer.devMode` must be `false` when the backend is in client-streaming mode and `true` when the backend uses its own microphone.

## Measurement runs (development only)

All measurements are off unless the backend runs with `VOXIGN_DEV_MODE=1` (the default) and the client is the editor or a development build.

- **Latency (NFR-01, DR-06).** Start the backend, keep Unity streaming (`VOXIGN_DEV_MODE=0`, `VoiceAudioStreamer.devMode` off), speak one utterance per pause, and stop. `Backend/Logs/latency.csv` holds the stage breakdown (`session`, `utterance`, `asr_ms`, `translate_ms`, `lookup_ms`, `encode_ms`, `broadcast_ms`, `pipeline_ms`, `total_ms`) and Unity's `dev_metrics.csv` holds the render half. In backend-microphone mode the CSV still records the stages after the final transcript, but the audio-to-transcript stage is not available.
- **Frame rate (NFR-02).** Any playback run writes the average and lowest frame rate for each utterance to `dev_metrics.csv`. On device the file is in the app's persistent data directory (`/storage/emulated/0/Android/data/<package>/files`).
- **Reconnect (NFR-03).** Interrupt the network or stop the backend during a session; each restored connection writes the outage length as a `reconnect` row and a `[DevMetrics]` log line.
- **Word error rate (FR-02).** Speak the reference phrases in order with the backend in DEV_MODE, then run `python Backend/evaluate_wer.py --references <file>`.

## Lifecycle summary

1. `VoxignMobileUI` starts; `AppBootstrap` loads `Main` additively (skipped if already present in the editor).
2. `Main` starts `SequenceReceiver` (connects, sends `hello`) and `VoiceAudioStreamer` (waits for the toggle or `devMode` setting).
3. `TranscriptionUI` finds the receiver/player and subscribes; `CameraOrbit` frames the avatar.
4. The toggle starts capture; the streamer sends `audio_config` and μ-law frames.
5. The backend decodes audio → Deepgram → T5 → WLASL → gzip sequence, sent back to that session only.
6. `SequenceReceiver` decodes and queues signs with their transcript; when the first sign of an utterance starts, `SignPlayer` raises `UtteranceStarted` (transcript panel) and `SignStarted` (chip); playback ends and the chip hides.
