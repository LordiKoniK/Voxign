import json
import numpy as np
import os

# Selection options, checked in this order: word sequence, video ID, single gloss
target_sequence = ["happy", "yes", "hello"]       # Words to sign in order, e.g. ["hello", "yes", "book"], or [] to skip
target_video_id = "69546"                        # Specific video ID (used primarily for testing purposes), leave as "" for empty
target_gloss = "yes"                             # Single word mode, picks the best-tracked entry for a given word

# Drop leading/trailing frames with no hands detected so signs join without dropping to rest
trim_idle_frames = True

# File paths for data input and output
data_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'Data')
output_path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'Assets', 'test_sequence.json')


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



# Create a series of frames for a given sign
def build_series(index, gloss, video_id):
    video_data = landmarks_dict[str(index)]
    original_frames = video_data.shape[0]
    if trim_idle_frames:
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


# Load dataset dictionary
print("Loading WLASL_parsed_data.json...")
with open(os.path.join(data_dir, 'WLASL_parsed_data.json'), 'r') as f:
    parsed_data = json.load(f)

# Load NPZ landmark archive (the dataset itself)
print("Loading landmarks_V3.npz...")
landmarks_dict = np.load(os.path.join(data_dir, 'landmarks_V3.npz'), allow_pickle=True)
available_keys = set(landmarks_dict.files)

# Index entries by 1. gloss and 2. video ID
entries_by_gloss = {}
index_by_video_id = {}
for index, entry in enumerate(parsed_data):
    if str(index) not in available_keys:
        continue
    # Extract ID from the video_path field (e.g., ".../64290.mp4" -> "64290") if a video ID is provided
    video_id = os.path.splitext(os.path.basename(entry.get('video_path', '')))[0]
    gloss = entry.get('gloss', 'unknown')
    entries_by_gloss.setdefault(gloss.lower(), []).append((index, video_id, gloss))
    index_by_video_id[video_id] = (index, video_id, gloss)


# If multiple entries exist for a word, select the one with the most hand-tracked frames
def find_best_entry(word):
    candidates = entries_by_gloss.get(word.lower(), [])
    if not candidates:
        return None
    return max(candidates, key=lambda c: hand_tracked_mask(landmarks_dict[str(c[0])]).mean())


# Overarching logic flow for landmark extraction
if target_sequence:                                                         # If word sequence given
    selections = [] 
    for word in target_sequence:
        match = find_best_entry(word)                                       # Find best landmark entry for each word
        if match is None:
            print(f"Warning: no recording found for '{word}', skipping.")   # Error handling
        else:
            selections.append(match)                                        # Add to list of selected entries
elif target_video_id:
    match = index_by_video_id.get(target_video_id)              # Selection is direct if specific video ID is given
    selections = [match] if match else []
else:
    match = find_best_entry(target_gloss)                       # If 1 word is given, get its best entry
    selections = [match] if match else []

if selections:
    print(f"Processing {len(selections)} sign(s)...")           # Convert the selected signs into structured frame data
    signs = [build_series(index, gloss, video_id) for index, video_id, gloss in selections]

    sequence_data = {
        "glossSequence": [sign["glossLabel"] for sign in signs],
        "signs": signs
    }

    # Save the frame sequence data as a JSON file
    with open(output_path, 'w') as f:
        f.write(json.dumps(sequence_data))

    print(f"Sequence {sequence_data['glossSequence']} saved to {output_path}.")       
else:
    print("Could not find any matching recordings in the dataset.")