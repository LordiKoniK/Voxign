import os
import re
import json
import asyncio
import websockets
import pyaudio
import torch
from dotenv import load_dotenv
from transformers import AutoTokenizer, AutoModelForSeq2SeqLM

# Updated version of SpeechRecognizer, with the addition of integrating the English-ASL translation via the T5 model

# Load API key from env 
load_dotenv()

# Audio settings 
FORMAT = pyaudio.paInt16
CHANNELS = 1
RATE = 16000
CHUNK = 1024

# Model directory
MODEL_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'Models', 't5_asl_gloss')


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


async def microphone_stream(translator):
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
            loop = asyncio.get_running_loop()

            # Helper function to read audio
            def get_audio():
                return stream.read(CHUNK, exception_on_overflow=False)

            async def sender():
                try:
                    while True:
                        # Offload blocking audio read to prevent event loop freezing
                        data = await loop.run_in_executor(None, get_audio)
                        await ws.send(data)
                except asyncio.CancelledError:
                    pass
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
                        if not transcript:
                            continue

                        # Run inference off of main event loop so audio keeps streaming
                        gloss = await loop.run_in_executor(None, translator.translate, transcript)
                        print(f"English:   {transcript}")
                        print(f"ASL Gloss: {gloss}\n")
                except asyncio.CancelledError:
                    pass
                except Exception as e:
                    print(f"Receiver error: {e}")

            # Run both sending and receiving concurrently to minimize downtime
            await asyncio.gather(sender(), receiver())

    except Exception as e:
        print(f"Connection error: {e}")
    finally:
        # Clean shutdown
        stream.stop_stream()
        stream.close()
        p.terminate()

# Integration with translation model
if __name__ == "__main__":
    if not os.path.exists(os.path.join(MODEL_DIR, 'gloss_config.json')):
        print(f"Error: trained model not found in {MODEL_DIR}. Unzip the notebook's t5_asl_gloss.zip there.")
    else:
        # Load model before connecting since Deepgram closes idle connections after a few seconds
        print("Loading T5 gloss model...")
        gloss_translator = GlossTranslator(MODEL_DIR)
        try:
            asyncio.run(microphone_stream(gloss_translator))
        except KeyboardInterrupt:
            print("\nTranscription stopped cleanly.")
