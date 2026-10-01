import rerun as rr
import json
import numpy as np
from pathlib import Path

# 1. Initialize Rerun and spawn the viewer window
rr.init("MediaPipe_Visualizer", spawn=True)

# 2. Load the exact payload Unity is receiving
print("Loading test_sequence.json...")
project_root = Path(__file__).resolve().parent.parent
with (project_root / 'test_sequence.json').open('r') as f:
    series_data = json.load(f)

print(f"Visualizing '{series_data['glossLabel']}' - {series_data['frameCount']} frames")

# 3. Step through the timeline and plot the points
for frame in series_data['frames']:
    frame_idx = frame['frameIndex']
    
    # Set Rerun's timeline to the current frame
    rr.set_time("frame", sequence=frame_idx)

    # Convert the list of dicts back into numpy arrays for easy plotting
    pose_pts = np.array([[p['x'], p['y'], p['z']] for p in frame['poseLandmarks']])
    left_hand_pts = np.array([[p['x'], p['y'], p['z']] for p in frame['handLandmarksLeft']])
    right_hand_pts = np.array([[p['x'], p['y'], p['z']] for p in frame['handLandmarksRight']])

    if len(pose_pts) >= 17:
        # Isolate the arm joints to draw connecting lines
        # MediaPipe: 11 (L Shoulder), 13 (L Elbow), 15 (L Wrist)
        # MediaPipe: 12 (R Shoulder), 14 (R Elbow), 16 (R Wrist)
        left_arm = [pose_pts[11], pose_pts[13], pose_pts[15]]
        right_arm = [pose_pts[12], pose_pts[14], pose_pts[16]]

        # Log Left Arm & Hand (BLUE)
        rr.log("signer/left/arm", rr.LineStrips3D([left_arm], colors=[0, 100, 255]))
        rr.log("signer/left/hand", rr.Points3D(left_hand_pts, colors=[0, 100, 255], radii=0.01))
        
        # Log Right Arm & Hand (RED)
        rr.log("signer/right/arm", rr.LineStrips3D([right_arm], colors=[255, 50, 50]))
        rr.log("signer/right/hand", rr.Points3D(right_hand_pts, colors=[255, 50, 50], radii=0.01))
        
        # Log the rest of the upper body (GREY)
        upper_body = np.concatenate([pose_pts[11:13], pose_pts[0:11]]) # Shoulders + Face
        rr.log("signer/body", rr.Points3D(upper_body, colors=[150, 150, 150], radii=0.015))

print("Data streamed to Rerun.")