import json
import numpy as np
import os
import re

# Standalone mode options, checked in this order: word sequence, video ID, single gloss
target_sequence = ["happy", "yes", "hello"]       # Words to sign in order, e.g. ["hello", "yes", "book"], or [] to skip
target_video_id = "69546"                        # Specific video ID (used primarily for testing purposes), leave as "" for empty
target_gloss = "yes"                             # Single word mode, picks the best-tracked entry for a given word

# Constant names changed to uppercase to allow use as function arguments cleanly while keeping the same name
# Controls whether idle frames (with no hands tracked) are trimmed from the start and end of each sign sequence
TRIM_IDLE_FRAMES = True

# File paths for data input and output
DATA_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'Data')
OUTPUT_PATH = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'Assets', 'test_sequence.json')

# Deepgram returns words as figures, WLASL expects them as words; this is a bridge for the common ones
# NB: Only some of these actually have signs in WLASL; those without their own proper sign are signed digit by digit (shown later)
NUMBER_WORDS = {
    0: "zero", 1: "one", 2: "two", 3: "three", 4: "four", 5: "five", 6: "six", 7: "seven", 8: "eight", 9: "nine",
    10: "ten", 11: "eleven", 12: "twelve", 13: "thirteen", 14: "fourteen", 15: "fifteen", 16: "sixteen",
    17: "seventeen", 18: "eighteen", 19: "nineteen", 20: "twenty", 30: "thirty", 40: "forty", 50: "fifty",
    60: "sixty", 70: "seventy", 80: "eighty", 90: "ninety", 100: "hundred", 1000: "thousand"
}


# Helper function for processing landmark data
def to_points(landmarks):
    return [{"x": float(pt[0]), "y": float(pt[1]), "z": float(pt[2])} for pt in landmarks]


# Determine which frames have the hands tracked (for idle trimming)
def hand_tracked_mask(video_data):
    return np.any(video_data[:, 0:42, :] != 0, axis=(1, 2))


# Trim leading/trailing frames of signs (prevents avatar from always returning to "arms-down" rest position between signs)
def trim_idle(video_data):
    tracked = np.where(hand_tracked_mask(video_data))[0]
    if len(tracked) == 0:
        return video_data
    return video_data[tracked[0]:tracked[-1] + 1]

# Loads the WLASL landmark dataset once and builds sign sequences from it
class LandmarkLibrary:

    def __init__(self, data_dir=DATA_DIR, trim_idle_frames=TRIM_IDLE_FRAMES):
        self.trim_idle_frames = trim_idle_frames

        # Load dataset dictionary
        print("Loading WLASL_parsed_data.json...")
        with open(os.path.join(data_dir, 'WLASL_parsed_data.json'), 'r') as f:
            parsed_data = json.load(f)

        # Load NPZ landmark archive (the dataset itself)
        print("Loading landmarks_V3.npz...")
        self.landmarks_dict = np.load(os.path.join(data_dir, 'landmarks_V3.npz'), allow_pickle=True)
        available_keys = set(self.landmarks_dict.files)

        # Index entries by 1. gloss and 2. video ID
        self.entries_by_gloss = {}
        self.entries_by_video_id = {}
        for index, entry in enumerate(parsed_data):
            if str(index) not in available_keys:
                continue
            # Extract ID from the video_path field (e.g., ".../64290.mp4" -> "64290")
            video_id = os.path.splitext(os.path.basename(entry.get('video_path', '')))[0]
            gloss = entry.get('gloss', 'unknown')
            self.entries_by_gloss.setdefault(gloss.lower(), []).append((index, video_id, gloss))
            self.entries_by_video_id[video_id] = (index, video_id, gloss)

        # Longest multi-word gloss in the dataset (e.g. "hard of hearing")
        self.max_phrase_words = max(len(g.split()) for g in self.entries_by_gloss)
        self._best_entries = {}

    # Check if a sign exists in the dataset for a given word
    def has_gloss(self, word):
        return word.lower() in self.entries_by_gloss

    # If multiple entries exist for a word, select the one with the most hand-tracked frames
    def find_best_entry(self, word):
        key = word.lower()
        if key not in self._best_entries:
            candidates = self.entries_by_gloss.get(key, [])
            self._best_entries[key] = max(
                candidates, key=lambda c: hand_tracked_mask(self.landmarks_dict[str(c[0])]).mean()
            ) if candidates else None
        return self._best_entries[key]

    def find_by_video_id(self, video_id):
        return self.entries_by_video_id.get(video_id)

    # Convert a gloss string (e.g. "ME HAVE 2 BROTHER") into individual WLASL lookup words
    def resolve_gloss(self, gloss):
        tokens = gloss.lower().split()
        words = []
        i = 0
        while i < len(tokens):
            # Prefer the longest multi-word gloss (i.e. look for "thank you" instead of "thank" + "you")
            for length in range(min(self.max_phrase_words, len(tokens) - i), 0, -1):
                phrase = " ".join(tokens[i:i + length])
                if self.has_gloss(phrase):
                    words.append(phrase)
                    i += length
                    break
            else:
                words += self._spell_out(tokens[i])
                i += 1
        return words

    # Fallback for tokens with no sign at all: numbers become number words (e.g. "45" -> "four" + "five"), anything else is fingerspelled
    def _spell_out(self, token):
        if token.isdigit():
            word = NUMBER_WORDS.get(int(token))
            if word and self.has_gloss(word):
                return [word]
            return [NUMBER_WORDS[int(digit)] for digit in token]

        letters = [c for c in token if c.isalpha()]
        missing = sorted({c for c in letters if not self.has_gloss(c)})
        if missing:
            print(f"Warning: no sign for letter(s) {missing} while fingerspelling '{token}', skipping them.")
        return [c for c in letters if self.has_gloss(c)]

    # Create a series of frames for a given sign
    def build_series(self, index, video_id, gloss):
        video_data = self.landmarks_dict[str(index)]
        original_frames = video_data.shape[0]
        if self.trim_idle_frames:
            video_data = trim_idle(video_data)

        frames = []
        for frame_index in range(video_data.shape[0]):
            frames.append({
                "frameIndex": frame_index,
                "handLandmarksLeft": to_points(video_data[frame_index, 21:42, :]),
                "handLandmarksRight": to_points(video_data[frame_index, 0:21, :]),
                "poseLandmarks": to_points(video_data[frame_index, 42:75, :])
            })

        print(f"  '{gloss}' (video {video_id}, index {index}): {len(frames)}/{original_frames} frames")

        return {
            "glossLabel": gloss,
            "frameCount": len(frames),
            "frames": frames
        }

    # Build Unity payload (from the standalone testing mode options at the top of the file)
    def build_sequence_from_entries(self, entries):
        if not entries:
            return None
        print(f"Processing {len(entries)} sign(s)...")
        signs = [self.build_series(index, video_id, gloss) for index, video_id, gloss in entries]
        return {
            "glossSequence": [sign["glossLabel"] for sign in signs],
            "signs": signs
        }

    # Build the Unity payload for a list of WLASL words, skipping anything that are fully absent from the dataset 
    # The fingerspelling fallback is still accounted for, the two mechanisms are joined at a higher level in VoxignPipeline 
    # This explicit skip was added because some letters themselves are also missing from the dataset and must be handled
    def build_sequence(self, words):
        entries = []
        for word in words:
            match = self.find_best_entry(word)
            if match is None:
                print(f"Warning: no recording found for '{word}', skipping.")
            else:
                entries.append(match)
        return self.build_sequence_from_entries(entries)


# Save the frame sequence data as a JSON file
def export_sequence(sequence_data, output_path=OUTPUT_PATH):
    # Write then swap so Unity never reads a half-written file (Unity ignores .tmp files)
    temp_path = output_path + ".tmp"
    with open(temp_path, 'w') as f:
        f.write(json.dumps(sequence_data))
    os.replace(temp_path, output_path)
    print(f"Sequence {sequence_data['glossSequence']} saved to {output_path}.")


# Overall logic flow for landmark extraction
def main():
    library = LandmarkLibrary()

    if target_sequence:
        sequence_data = library.build_sequence(target_sequence)
    elif target_video_id:
        match = library.find_by_video_id(target_video_id)
        sequence_data = library.build_sequence_from_entries([match] if match else [])
    else:
        sequence_data = library.build_sequence([target_gloss])

    if sequence_data:
        export_sequence(sequence_data)
    else:
        print("Could not find any matching recordings in the dataset.")


if __name__ == "__main__":
    main()