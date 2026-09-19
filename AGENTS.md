# RNI Palette

0.6 is in development and sandbox integration, not a completed release. The user's 0.4 apply/switch/repeat-click/favorites acceptance remains the baseline; 0.5 failed before sending because its two-second UIA read timed out. Current work implements all installed film paths, selective RNI clear and primary-by-primary batch use. Live outcomes belong in README/release notes, not inferred from compilation. Broader local context: `D:\照片\AGENTS.md`.

## Work

- Single-user Windows / C# / .NET Framework WinForms tool. Prioritize features and user feedback; compile changed code, but do not keep adding tests, repeating full regressions, or scanning file checksums.
- The user explicitly authorized autonomous development and sandbox testing while away. Exactly one designated executor may control C1; parallel agents handle code, docs and read-only research. Recheck the live window, full document path and selection before every integration session. Do not repeat already accepted flows without a concrete reason. Physical Esc stops control.
- No C1 restart, direct catalog writes, original-media changes, overwritten snapshots, vendor-asset redistribution or silent C1 setting changes. Agent-driven photo changes are confined to a freshly confirmed sandbox selection. Main-catalog connection support is not permission for agent-driven work-catalog edits.
- First requirement: a fast Capture One film entry point with single/multiple selection and Clear Current RNI, never reset-all. Automatic connection is secondary. Do not redo the completed photo migration.
- Architecture is UI Automation + native UI actions/SendInput + read-only identity lookup, not an official selection/style API. Windows SDK business capabilities remain unverified; private methods and macOS AppleScript are not external Windows interfaces.
- Keep empty, hidden, missing, unreadable and timed-out style observations distinct. Do not treat lazy source-checkbox Off as proof of absence. `rni-name:` recognizes an installed RNI name, not an initial UUID; precise row removal and source-path application must preserve this evidence distinction.
- Batch must observe live primary-only editing and independently confirm each target. Never reuse a primary checkmark for the whole selection or simply remove a multi-selection guard. On an uncertain send, stop without retry and report the confirmed prefix. Distinguish sent, native-confirmed and live-feature-verified results.
- Code: `src/`; existing tests: `tests/CoreTests.cs`. Build alongside running releases with `build.ps1 -OutputDirectory <new-directory>`; deliver under the active task's `outputs/`.
- Preserve shared favorites/window placement in `%LOCALAPPDATA%\RniPalette`; seed only if absent. Supported catalogs are the explicitly named Photography-Master work catalog and RNI-Panel-Sandbox.

## Git

- `git@github.com:VectorWang2015/C1-RNI-Panel.git`, branch `main`. Commit and push each coherent completed change, including documentation updates; inspect the staged diff. Never force-push or overwrite others' work.
- Track source, tests, build script and docs only. Keep builds, credentials, personal state/logs, local photo fixtures, catalogs, media and vendor assets ignored.
- Existing SSH public key is registered on the user's GitHub account; no key changes or proxy were needed. On failure, retain the local commit and report it.
