import json
import numpy as np
import os

# Optional configuration to select specific video
target_video_id = "69546"  # Specific video ID, or leave as "" for empty
target_gloss = "yes"       # Secondary option, choose a word and it will pick one of its entries

data_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'Data')

# Load JSON metadata
print("Loading WLASL_parsed_data.json...")
with open(os.path.join(data_dir, 'WLASL_parsed_data.json'), 'r') as f:
    parsed_data = json.load(f)

# Load NPZ landmark archive
print("Loading landmarks_V3.npz...")
landmarks_dict = np.load(os.path.join(data_dir, 'landmarks_V3.npz'), allow_pickle=True)

# Search for target sequence
match_index = -1
matched_gloss = ""

for index, entry in enumerate(parsed_data):
    # Extract ID from the video_path (e.g., ".../64290.mp4" -> "64290")
    current_path = entry.get('video_path', '')
    current_id = os.path.splitext(os.path.basename(current_path))[0]
    
    # Prioritize explicit ID search
    if target_video_id:
        if current_id == target_video_id:
            match_index = index
            matched_gloss = entry.get('gloss', 'unknown')
            print(f"Exact video ID match found! Array Index: {index}, Gloss: '{matched_gloss}'")
            break
        
    # Fallback to searching by gloss
    else:
        if entry.get('gloss') == target_gloss:
            match_index = index
            matched_gloss = target_gloss
            print(f"Gloss match found! Array Index: {index}, Video ID: {current_id}")
            break

# Extract and format the data if found
if match_index != -1:
    npz_key = str(match_index)
    
    if npz_key in landmarks_dict:
        video_data = landmarks_dict[npz_key]
        num_frames = video_data.shape[0]
        
        print(f"Successfully loaded video data for '{matched_gloss}' (Array Index {npz_key}).")
        print(f"Processing {num_frames} frames...")
        
        unity_payload = []
        
        for frame_index in range(num_frames):
            right_hand = video_data[frame_index, 0:21, :]
            left_hand = video_data[frame_index, 21:42, :]
            pose = video_data[frame_index, 42:75, :]
            
            # Cast numpy types to native Python floats for JSON serialization
            frame_data = {
                "frameIndex": frame_index,
                "handLandmarksLeft": [{"x": float(pt[0]), "y": float(pt[1]), "z": float(pt[2])} for pt in left_hand],
                "handLandmarksRight": [{"x": float(pt[0]), "y": float(pt[1]), "z": float(pt[2])} for pt in right_hand],
                "poseLandmarks": [{"x": float(pt[0]), "y": float(pt[1]), "z": float(pt[2])} for pt in pose]
            }
            unity_payload.append(frame_data)
            
        # Wrap payload in the defined LandmarkSeries structure
        series_data = {
            "glossLabel": matched_gloss,
            "frameCount": num_frames,
            "frameRate": 30,
            "frames": unity_payload
        }

        # Export the sequence
        full_json_output = json.dumps(series_data)
        with open('test_sequence.json', 'w') as f:
            f.write(full_json_output)
            
        # Feedback logs
        print("Sequence saved to test_sequence.json. Ready to paste into Unity's PipelineTester.")
    else:
        print(f"Error: Index {npz_key} is missing from the NPZ file.")
else:
    search_type = f"Video ID '{target_video_id}'" if target_video_id else f"Gloss '{target_gloss}'"
    print(f"Could not find a match for {search_type} in the JSON file.")