import rerun as rr
import json
import numpy as np
from pathlib import Path

# Script for visualizing MediaPipe landmark data using the Rerun library
# Used during testing/development to verify the data being streamed to the Unity avatar

# Initialize Rerun and create viewer window
rr.init("MediaPipe_Visualizer", spawn=True)

# Create edges between hand joints for visualization
HAND_CHAINS = [[0, 1, 2, 3, 4], [0, 5, 6, 7, 8], [0, 9, 10, 11, 12], [0, 13, 14, 15, 16], [0, 17, 18, 19, 20], [5, 9, 13, 17]]

# Separate hand colours for clarity
LEFT_COLOR = [0, 100, 255]
RIGHT_COLOR = [255, 50, 50]

# Coordinate conversion functions
def to_array(points):
    return np.array([[p['x'], p['y'], p['z']] for p in points]).reshape(-1, 3)


def is_tracked(points):
    return points.size > 0 and np.any(points != 0)


def to_avatar_space(points, aspect):
    # Same conversion used on the Unity side
    return np.column_stack([-points[:, 0] * aspect, -points[:, 1], -points[:, 2] * aspect])


# Data logging functions for arm and hand landmarks
def log_arm(path, pose, shoulder, elbow, wrist, color, aspect):
    joints = pose[[shoulder, elbow, wrist]]
    if np.any(np.all(joints == 0, axis=1)):
        rr.log(path, rr.Clear(recursive=True))
        return None
    joints = to_avatar_space(joints, aspect)
    rr.log(path, rr.LineStrips3D([joints], colors=color))
    return joints[2]


def log_hand(path, hand, pose_wrist, color, aspect):
    if not is_tracked(hand):
        rr.log(path, rr.Clear(recursive=True))
        return
    pts = to_avatar_space(hand, aspect)
    if pose_wrist is not None:
        pts[:, 2] += pose_wrist[2] - pts[0, 2]
    rr.log(f"{path}/bones", rr.LineStrips3D([pts[chain] for chain in HAND_CHAINS], colors=color))
    rr.log(f"{path}/joints", rr.Points3D(pts, colors=color, radii=0.008))


# Load the exact payload Unity is receiving
print("Loading test_sequence.json...")
project_root = Path(__file__).resolve().parent.parent
with (project_root / 'Assets' / 'test_sequence.json').open('r') as f:
    data = json.load(f)

# Show list of all the signs in the payload (in order) for context
signs = data['signs'] if 'signs' in data else [data]
print(f"Visualizing {len(signs)} sign(s): {[sign['glossLabel'] for sign in signs]}")

rr.log("signer", rr.ViewCoordinates.LEFT_HAND_Y_UP, static=True)

# Final result: Continuous timeline of the sign data as Unity sees it
global_frame = 0
elapsed = 0.0
for sign in signs:
    aspect = sign.get('aspectRatio') or 1.0
    frame_rate = sign.get('frameRate') or 30.0

    rr.set_time("frame", sequence=global_frame)
    rr.set_time("playback", duration=elapsed)
    rr.log("gloss", rr.TextLog(f"{sign['glossLabel']} ({len(sign['frames'])} frames, {frame_rate:.0f} fps)"))

    for frame in sign['frames']:
        rr.set_time("frame", sequence=global_frame)
        rr.set_time("playback", duration=elapsed)

        pose_pts = to_array(frame['poseLandmarks'])
        left_hand_pts = to_array(frame['handLandmarksLeft'])
        right_hand_pts = to_array(frame['handLandmarksRight'])

        left_wrist = right_wrist = None
        if len(pose_pts) >= 17 and is_tracked(pose_pts):
            left_wrist = log_arm("signer/left/arm", pose_pts, 11, 13, 15, LEFT_COLOR, aspect)
            right_wrist = log_arm("signer/right/arm", pose_pts, 12, 14, 16, RIGHT_COLOR, aspect)

            upper_body = to_avatar_space(np.concatenate([pose_pts[11:13], pose_pts[0:11]]), aspect)
            rr.log("signer/body", rr.Points3D(upper_body, colors=[150, 150, 150], radii=0.015))
        else:
            rr.log("signer/body", rr.Clear(recursive=True))

        log_hand("signer/left/hand", left_hand_pts, left_wrist, LEFT_COLOR, aspect)
        log_hand("signer/right/hand", right_hand_pts, right_wrist, RIGHT_COLOR, aspect)

        global_frame += 1
        elapsed += 1.0 / frame_rate

print("Data streamed to Rerun.")