import asyncio
import json
from concurrent.futures import ThreadPoolExecutor

from websockets.asyncio.server import broadcast, serve

from LandmarkExtractor import OUTPUT_PATH, LandmarkLibrary, export_sequence
from SpeechToGloss import load_translator, microphone_stream

# Localhost only for now, will be expanded to allow the Android app connection later
HOST = "127.0.0.1"
PORT = 8765

# WebSocket server that tells connected clients when a new sequence is exported
class UnityBridge:

    def __init__(self):
        self.clients = set()
        
    # Manages WebSocket connections and disconnections
    async def handler(self, connection):
        self.clients.add(connection)
        print(f"[Unity] Connected ({len(self.clients)} client(s)).")
        try:
            await connection.wait_closed()
        finally:
            self.clients.discard(connection)
            print(f"[Unity] Disconnected ({len(self.clients)} client(s)).")
    
    # Notifies clients when a new sequence is exported (i.e. when test_sequence.json is updated)
    def notify(self, message):
        # If there's no connected clients, skip broadcasting but still save the JSON
        if not self.clients:
            print("[Unity] No client connected; sequence saved but not played.")
        broadcast(self.clients, json.dumps(message))

# Bringing the whole pipeline together (Speech -> English transcript -> ASL gloss -> WLASL landmark sequence -> JSON -> Unity)
class SpeechToSignPipeline:

    def __init__(self, output_path=OUTPUT_PATH):
        self.output_path = output_path
        # Load everything up front, since Deepgram closes idle connections after a few seconds
        self.translator = load_translator()
        self.library = LandmarkLibrary()
        self.bridge = UnityBridge()
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
            self.bridge.notify({
                "type": "sequence",
                "english": transcript,
                "gloss": gloss,
                "glossSequence": sequence["glossSequence"],
            })
    
    async def run(self):
        async with serve(self.bridge.handler, HOST, PORT):
            print(f"Unity bridge listening on ws://{HOST}:{PORT}")
            await microphone_stream(self.on_transcript)


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
