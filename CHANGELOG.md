# Changelog

All notable changes to Voxign are recorded here. The project follows a semantic versioning scheme for its milestones.

## [Unreleased]

### Added

- Repository governance documentation: the README now covers the problem, configuration, testing, releases and access; `CONTRIBUTING.md`, `CHANGELOG.md`, `Docs/TASKS.md` and `LICENSE` record the workflow, release history, task register and access terms.
- Annotated release tags `v0.1.0` and `v1.0.0` (local until pushed).

### Security

- The production authentication token must remain outside version control. `Assets/Scenes/Main.unity` is intentionally not included in the documentation commit while it contains the production token.

### Known gaps

- Formal latency, frame-rate, word-error, sign-intelligibility and System Usability Scale evaluations are not implemented.
- No GitHub Actions workflow or Codespaces environment is configured; hosted-service allowances were not used.
- No feature-branch or pull-request history was retained for the consolidated implementation phase.

## [v1.0.0] - 2026-10-29

### Added

- Authenticated WSS production transport with keepalive and exponential reconnect.
- Client microphone capture with Android permission handling and G.711 mu-law compression.
- Deepgram streaming transcription, T5-small gloss translation and WLASL landmark lookup.
- gzip-and-base64 sequence payloads and a bounded per-client audio queue.
- Docker, Docker Compose and Caddy deployment on Oracle Cloud Always Free.
- Mobile UI with voice toggle, English transcript, current-sign chip and orbit/zoom camera.
- `README.md`, `Docs/Scripts.md`, `Deploy/` configuration and the load-test client.
- Mobile UI fixes for normal-map import, duplicate scene loading, camera framing and avatar detail.

### Reference

- Commit `f8cc4a4`; tag `v1.0.0`.

## [v0.1.0] - 2026-10-25

### Added

- Mobile UI layer scene with the sidebar, transcription panel, voice toggle and refined sprites.
- Voice toggle script and the supporting UI assets.
- Initial mobile UI scaffolding and scene composition.

### Reference

- Commit `72add90`; tag `v0.1.0`.

## Earlier history

- `7503d09` began the mobile UI work.
- `51a7715` integrated the remote `main` branch.
- `d3051d0` and `b2b365d` added the README.
- `4f09caa` configured the Git LFS attributes.
