import os
import asyncio
import websockets
import json
import pyaudio
from dotenv import load_dotenv

# Load API key from env 
load_dotenv()

# Audio settings 
FORMAT = pyaudio.paInt16
CHANNELS = 1
RATE = 16000
CHUNK = 1024

async def microphone_stream():
    api_key = os.getenv("DEEPGRAM_API_KEY")
    if not api_key:
        print("Error: DEEPGRAM_API_KEY not found in .env file.")
        return

    # Explicitly define parameters in the URL to avoid SDK serialization bugs
    url = "wss://api.deepgram.com/v1/listen?model=nova-3&encoding=linear16&sample_rate=16000&channels=1&smart_format=true"
    headers = {"Authorization": f"Token {api_key}"}

    print("Initializing microphone...")
    p = pyaudio.PyAudio()
    stream = p.open(format=FORMAT, channels=CHANNELS, rate=RATE, input=True, frames_per_buffer=CHUNK)

    print("Connecting to Deepgram ...")
    try:
        async with websockets.connect(url, additional_headers=headers) as ws:
            print("Microphone active. Listening... (Press Ctrl+C to stop)")

            # Helper function to read audio
            def get_audio():
                return stream.read(CHUNK, exception_on_overflow=False)

            async def sender():
                try:
                    while True:
                        # Offload blocking audio read to prevent event loop freezing
                        data = await asyncio.get_event_loop().run_in_executor(None, get_audio)
                        await ws.send(data)
                except asyncio.CancelledError:
                    pass
                except Exception as e:
                    print(f"Sender error: {e}")

            async def receiver():
                try:
                    async for message in ws:
                        res = json.loads(message)
                        if res.get("type") == "Results":
                            alts = res.get("channel", {}).get("alternatives", [])
                            if alts and alts[0].get("transcript"):
                                print(f"Deepgram: {alts[0]['transcript']}")
                except asyncio.CancelledError:
                    pass
                except Exception as e:
                    print(f"Receiver error: {e}")

            # Run both sending and receiving concurrently
            await asyncio.gather(sender(), receiver())

    except Exception as e:
        print(f"Connection error: {e}")
    finally:
        # Clean shutdown
        stream.stop_stream()
        stream.close()
        p.terminate()

if __name__ == "__main__":
    try:
        asyncio.run(microphone_stream())
    except KeyboardInterrupt:
        print("\nTranscription stopped cleanly.")