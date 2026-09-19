# RNI Palette

0.4 is delivered and being tested by the user; 0.3 native readback/round-trip/repeat behavior is accepted. Broader photo context: `D:\照片\AGENTS.md`.

## Work

- Single-user Windows / C# / .NET Framework WinForms tool. Prioritize features and user feedback; compile changed code, but do not keep adding tests, repeating full regressions, or scanning file checksums.
- User handles UI testing. Do not take over their mouse or repeat accepted flows. Keep protection against wrong targets and accidental style toggles.
- No direct catalog writes, original-media changes, bulk application, vendor-asset redistribution, or silent C1 setting changes. Main-catalog support requires the user's in-app connection confirmation, not agent-initiated photo edits.
- Code: `src/`; existing tests: `tests/CoreTests.cs`. Build alongside running releases with `build.ps1 -OutputDirectory <new-directory>`; deliver under the active task's `outputs/`.
- Preserve shared favorites/window placement in `%LOCALAPPDATA%\RniPalette`; seed only if absent. Supported catalogs are the explicitly named Photography-Master work catalog and RNI-Panel-Sandbox.

## Git

- `git@github.com:VectorWang2015/C1-RNI-Panel.git`, branch `main`. Commit and push each coherent completed change, including documentation updates; inspect the staged diff. Never force-push or overwrite others' work.
- Track source, tests, build script and docs only. Keep builds, credentials, personal state/logs, local photo fixtures, catalogs, media and vendor assets ignored.
- Existing SSH public key is registered on the user's GitHub account; no key changes or proxy were needed. On failure, retain the local commit and report it.
