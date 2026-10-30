# Contributing to Voxign

## Scope

Voxign is a private academic project. Contributions are limited to the author, the supervisor and authorised examiners. Access is granted by collaborator invitation to the GitHub repository; see [LICENSE](LICENSE).

## Repository and access

- Project repository: https://github.com/LordiKoniK/Voxign
- The `main` branch is the integrated, deployable state.
- The repository is private. Do not make it public without reviewing the third-party terms in [LICENSE](LICENSE) and removing local placeholders from scene configuration.

## Branches and pull requests

Use short-lived branches and pull requests for future changes:

1. Create `feature/<area>` or `fix/<area>` from `main`.
2. Keep the change focused on one task from [Docs/TASKS.md](Docs/TASKS.md).
3. Open a pull request into `main` with the task ID, the requirement or objective addressed, the test evidence and any limitations.
4. Review the diff, verify that no secret, dataset or build output is included, then merge and delete the branch.

The delivered history was committed directly to `main`, including the consolidated production commit `f8cc4a4` and the remote merge `51a7715`; no feature-branch or pull-request record was retained for that phase. This is a historical limitation. Future changes should use the branch and pull-request workflow above so that intent, review and test evidence are traceable.

## Commit messages

Use short, imperative messages with a conventional prefix:

- `feat(scope): add ...`
- `fix(scope): correct ...`
- `docs: update ...`
- `test: add ...`
- `deploy: configure ...`
- `chore: ...`

Reference the task ID from [Docs/TASKS.md](Docs/TASKS.md) in the body when applicable, for example `Task: T-05`. Keep commits small enough that each one represents one coherent change. Do not commit `.env` files, API keys, production authentication tokens, `Backend/Data`, `Backend/Models` archives or build outputs.

## Task and issue tracking

GitHub Issues are the preferred tracker when available. The current repository uses [Docs/TASKS.md](Docs/TASKS.md) as its traceable task register because no issue tracker was configured. Update the register when a task changes state, and link the commit or pull request that provides the evidence.

## Testing before a pull request

Run the checks in the README `Testing` section, or at minimum:

```bash
python Deploy/load_test.py --clients 3 --seconds 10
python Deploy/load_test.py --clients 1 --seconds 5 --audio
```

Confirm that the Unity project compiles and that the affected scene still loads. There is no continuous-integration runner in the repository; the checks are manual. If GitHub Actions is added, add the workflow to `.github/workflows/` and record its status in the pull request.

## Releases and tags

- Use semantic versions for milestones.
- Update [CHANGELOG.md](CHANGELOG.md) before tagging.
- Create an annotated tag, for example `git tag -a v1.0.1 -m "v1.0.1: ..."`.
- Push the tag with `git push origin <tag>` or `git push origin --tags`.
- Current milestones are `v0.1.0` (mobile UI) and `v1.0.0` (production integration).

## Dependencies and environment

- Backend development: `Backend/requirements.txt`.
- Backend production container: `Backend/requirements-prod.txt` and `Backend/Dockerfile`.
- Local and production deployment: `Deploy/docker-compose*.yml`, `Deploy/Caddyfile` and `Deploy/.env.example`.
- Unity: `Packages/manifest.json`, `Packages/packages-lock.json` and `ProjectSettings/ProjectVersion.txt`.
- Python versions and tested package versions are recorded in the README and in Chapter 5 of the project report.

## Student development services

Verified students should check GitHub Education before purchasing development services. GitHub Pro, Codespaces and Actions allowances can support private collaboration, reproducible environments and automated tests. At the time of the delivered implementation no Codespaces environment or Actions workflow was configured, and no hosted-service allowance was consumed; if these services are adopted, record their use and monitor the monthly Actions minutes, storage and Codespaces hours in the task register.
