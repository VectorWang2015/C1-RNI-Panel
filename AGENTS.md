# RNI Palette development

## Working agreement

- This is the source directory for a single-user Windows Capture One helper.
- The parent `D:\照片\AGENTS.md` has the migration context. Do not repeat completed photo work.
- User validates the native UI. Do not repeatedly take over their mouse or retest accepted flows.
- Keep validation lightweight: compile the changed app and use the user's actual-use feedback. Do not keep growing a test checklist or repeatedly run full regressions for this personal tool; retain existing tests without making them the main workflow.
- Do not routinely hash programs, catalogs, photographs, or vendor style files. Keep checks that prevent a wrong image/catalog or an unintended native toggle.
- No direct catalog database writes. No original-media changes, bulk style application, proprietary ICC/style redistribution, or silent C1 configuration changes.
- Main-catalog use is a product feature gated by the user's explicit in-app connection confirmation. It is not authority for the agent to edit main-catalog photos.

## Source and delivery

- `src/`: C# / .NET Framework Windows Forms application.
- `tests/CoreTests.cs`: local core and simulated-workflow tests; installed RNI styles are required for integration portions.
- `build.ps1 -OutputDirectory <new-directory>`: build beside, not over, a running release.
- Deliver application builds under the active Codex task's `outputs/`; never track binaries or local user state here.
- Preserve existing favorites. From 0.4, shared state is in `%LOCALAPPDATA%\RniPalette`, seeded only if absent.
- Runtime is restricted to the explicitly named Photography-Master work catalog and RNI-Panel-Sandbox; the user must connect the current catalog first.

## Git synchronization

- Remote: `git@github.com:VectorWang2015/C1-RNI-Panel.git`.
- After each coherent, buildable round of changes, commit and push the code. Do not leave synchronization as an implied future promise.
- Inspect the staged file list and diff before pushing. `.gitignore` is an allowlist: source, tests, build script, and docs only.
- Never force-push or overwrite remote work. Report authentication/network failures and keep the local commit.
- Do not store credentials, personal preferences, machine logs, photo/catalog data, RNI ICC/style files, or Capture One binaries in Git.
- SSH works after the user registered the machine's existing public key with their GitHub account. No key was created or changed, and no Git proxy configuration was needed.
