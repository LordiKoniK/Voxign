# Task Register

GitHub Issues were not configured for this repository. This register is the traceable task record for the delivered work. Each task links to the requirement or objective it addresses, its status, and the evidence or commit that supports it. The report's Chapter 5 contains the corresponding test and evaluation evidence.

| ID | Task | Requirement or objective | Status | Evidence or commit |
|---|---|---|---|---|
| T-01 | Analyse the problem landscape, existing systems and requirements | Objectives 1 and 2; Chapter 4 requirements | Done | Chapters 1, 2 and 4 of the report |
| T-02 | Implement the WLASL landmark library, gloss resolution and fingerspelling fallback | FR-04, FR-05, DR-01, DR-04 | Done | `Backend/LandmarkExtractor.py`; `f8cc4a4` |
| T-03 | Integrate the fine-tuned T5-small gloss translator | FR-03, DR-02, DR-03, IR-03 | Done | `Backend/SpeechToGloss.py`; `f8cc4a4` |
| T-04 | Implement client microphone capture, Android permission and mu-law compression | FR-01, IR-01 | Done | `Assets/Scripts/VoiceAudioStreamer.cs`; `f8cc4a4` |
| T-05 | Implement authenticated WSS transport, keepalive, reconnect and gzip payloads | FR-06, NFR-03, IR-05 | Done | `Assets/Scripts/SequenceReceiver.cs`; `Backend/VoxignPipeline.py`; `f8cc4a4` |
| T-06 | Implement landmark-to-bone mapping, IK constraints and sign playback blending | FR-07, FR-08, NFR-04 | Done | `LandmarkMapper.cs`, `IKSolver.cs`, `AnimationController.cs`, `SignPlayer.cs`; `f8cc4a4` |
| T-07 | Implement the mobile UI, transcript, current-sign chip and orbit camera | IR-01 | Done | `Assets/Scenes/VoxignMobileUI.unity`; `CameraOrbit.cs`; `TranscriptionUI.cs`; `72add90`, `f8cc4a4` |
| T-08 | Containerise and deploy the backend with TLS on Oracle Cloud | IR-06, deployment requirements | Done | `Backend/Dockerfile`; `Deploy/`; `v1.0.0` |
| T-09 | Add the authenticated load-test client and execute concurrency/audio checks | NFR-03 and concurrency testing support | Done | `Deploy/load_test.py`; `f8cc4a4`; Chapter 5 |
| T-10 | Correct the duplicate-scene, normal-map, camera and certificate issues | Defect fixes D-01 to D-04 | Done | `f8cc4a4`; Chapter 5, Table 5.4 |
| T-11 | Produce the README, script reference, contribution guide, changelog, task register and licence | Documentation and reproducibility | Done | Repository governance change; `README.md`, `Docs/Scripts.md`, `CONTRIBUTING.md`, `CHANGELOG.md`, `LICENSE` |
| T-12 | Run the formal fifty-phrase, latency, frame-rate, WER, intelligibility and SUS evaluations | Objective 5; NFR-01, NFR-02; DR-06 | Open | No measurements or participant data; Chapter 5, Table 5.5 |
| T-13 | Remove the production authentication token from the tracked scene and externalise build configuration | Security and DR-05 boundary | Open | `Assets/Scenes/Main.unity` working-tree change; must not be committed |
| T-14 | Add a GitHub Actions workflow, Codespaces configuration and branch/PR history | Reproducibility and collaboration | Open | No `.github/workflows` or `.devcontainer`; see Chapter 3 and `CONTRIBUTING.md` |
| T-15 | Create release milestones and annotated tags | Release history | Done | `v0.1.0` at `72add90`; `v1.0.0` at `f8cc4a4`; `CHANGELOG.md` |

## Notes

- `f8cc4a4` is the consolidated production commit; `72add90` is the mobile UI milestone. Earlier commits are listed in `CHANGELOG.md`.
- The task register is updated manually. When GitHub Issues becomes available, each task should be mirrored as an issue with the same ID and linked to its pull request.
- T-12 and T-13 are the two open items that most affect the strength of the evaluation and the security posture of the repository.
