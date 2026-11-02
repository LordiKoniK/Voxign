import os
import re
import json
import asyncio
import websockets
import torch
from dotenv import load_dotenv
from transformers import AutoTokenizer, AutoModelForSeq2SeqLM

# Load API key from env 
load_dotenv()

# Audio settings 
PA_INT16 = 8
CHANNELS = 1
RATE = 16000
CHUNK = 1024

# T5 model directory
MODEL_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'Models', 't5_asl_gloss')

# Deepgram streaming endpoint. The sample rate value is set as the rate the sender reports
# because not all devices may support an exact 16 kHz capture rate
DEEPGRAM_URL = (
    "wss://api.deepgram.com/v1/listen?model=nova-3&encoding=linear16"
    "&sample_rate={sample_rate}&channels=1&smart_format=true"
)

# Translates transcripts to gloss using the T5-small
class GlossTranslator:
    def __init__(self, model_dir):
        with open(os.path.join(model_dir, 'gloss_config.json'), 'r') as f:
            self.config = json.load(f)

        self.device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
        self.tokenizer = AutoTokenizer.from_pretrained(model_dir)
        self.model = AutoModelForSeq2SeqLM.from_pretrained(model_dir).to(self.device).eval()

    @torch.no_grad()
    def translate(self, text):
        # The model was trained on single sentences, so translate each one separately
        sentences = [s for s in re.split(r'(?<=[.!?])\s+', text.strip()) if s]
        if not sentences:
            return ""

        enc = self.tokenizer([self.config['task_prefix'] + s for s in sentences], max_length=self.config['max_source_len'],
                             truncation=True, padding=True, return_tensors="pt").to(self.device)
        generated = self.model.generate(**enc, max_new_tokens=self.config['max_target_len'], num_beams=self.config['num_beams'])
        glosses = self.tokenizer.batch_decode(generated, skip_special_tokens=True)
        return " ".join(" ".join(g.upper().split()) for g in glosses)


# Main/Production path: Streams raw PCM16 audio chunks from frontend to Deepgram and awaits on_transcript(text) per final transcript
async def stream_to_deepgram(chunks, on_transcript, sample_rate=RATE):
    api_key = os.getenv("DEEPGRAM_API_KEY")
    if not api_key:
        print("Error: DEEPGRAM_API_KEY not found in .env file.")
        return

    # Explicitly define parameters in the URL to avoid SDK serialization bugs
    url = DEEPGRAM_URL.format(sample_rate=sample_rate)
    headers = {"Authorization": f"Token {api_key}"}

    async with websockets.connect(url, additional_headers=headers) as ws:
        async def sender():
            try:
                async for chunk in chunks:
                    if chunk:
                        await ws.send(chunk)
                # Tells Deepgram the utterance is finished so it flushes the final transcript
                await ws.send(json.dumps({"type": "CloseStream"}))
            except asyncio.CancelledError:
                raise
            except Exception as e:
                print(f"Sender error: {e}")

        async def receiver():
            try:
                async for message in ws:
                    res = json.loads(message)
                    if res.get("type") != "Results" or not res.get("is_final", True):
                        continue

                    alts = res.get("channel", {}).get("alternatives", [])
                    transcript = alts[0].get("transcript", "") if alts else ""
                    if transcript:
                        await on_transcript(transcript)
            except asyncio.CancelledError:
                raise
            except Exception as e:
                print(f"Receiver error: {e}")

        # Run both sending and receiving concurrently to minimize downtime
        await asyncio.gather(sender(), receiver())


# DEV_MODE path: records from the machine running the backend and forwards it to Deepgram
# on_audio, when supplied, is called for every captured chunk so the caller can timestamp latency
async def microphone_stream(on_transcript, on_audio=None):
    api_key = os.getenv("DEEPGRAM_API_KEY")
    if not api_key:
        print("Error: DEEPGRAM_API_KEY not found in .env file.")
        return

    print("Initializing microphone...")
    import pyaudio
    p = pyaudio.PyAudio()
    try:
        stream = p.open(format=PA_INT16, channels=CHANNELS, rate=RATE, input=True, frames_per_buffer=CHUNK)
    except Exception as e:
        print(f"Could not open microphone: {e}")
        p.terminate()
        return

    print("Microphone active. Listening... (Press Ctrl+C to stop)")
    loop = asyncio.get_running_loop()

    # Helper function to read audio
    def read_audio():
        return stream.read(CHUNK, exception_on_overflow=False)

    async def chunks():
        while True:
            # Offload blocking audio read to prevent event loop freezing
            chunk = await loop.run_in_executor(None, read_audio)
            if on_audio is not None:
                on_audio()
            yield chunk

    try:
        await stream_to_deepgram(chunks(), on_transcript, RATE)
    except Exception as e:
        print(f"Connection error: {e}")
    finally:
        # Clean shutdown
        stream.stop_stream()
        stream.close()
        p.terminate()


def load_translator(model_dir=MODEL_DIR):
    if not os.path.exists(os.path.join(model_dir, 'gloss_config.json')):
        raise FileNotFoundError(f"Trained model not found in {model_dir}. Unzip the notebook's t5_asl_gloss.zip there.")
    print("Loading T5 gloss model...")
    return GlossTranslator(model_dir)


def main():
    try:
        # Load T5 before connecting, since Deepgram closes idle connections after a few seconds
        translator = load_translator()
    except FileNotFoundError as e:
        print(f"Error: {e}")
        return

    async def print_translation(transcript):
        # Run inference off of the main event loop so audio keeps streaming
        gloss = await asyncio.get_running_loop().run_in_executor(None, translator.translate, transcript)
        print(f"English:   {transcript}")
        print(f"ASL Gloss: {gloss}\n")

    try:
        asyncio.run(microphone_stream(print_translation))
    except KeyboardInterrupt:
        print("\nTranscription stopped cleanly.")


if __name__ == "__main__":
    main()
