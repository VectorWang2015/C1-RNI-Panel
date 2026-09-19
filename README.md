# RNI Palette

A small, single-user Windows companion for Capture One: film search, favorites, and real RNI 25 / 50 / 75 / 100 percent style variants.

This is an independent helper, not an embedded Capture One plugin. Capture One and the RNI styles must already be installed and licensed. No vendor ICC profiles, styles, program files, catalog data, or photographs are included.

## Current development version: 0.6

The first requirement is a quick film-style entry point for Capture One: select photos, click a film strength, or clear the current RNI effect. Version 0.5 was not accepted as usable: its first visible-style observation timed out before any style command was sent. Version 0.6 addresses that reader and removes the eight-command execution limit. Implementation and live validation are recorded separately below.

- The full installed collection is indexed by UUID and exact native tree path. On the development installation this is 1,680 styles / 426 families. Existing native shortcuts are discovered from their saved UUID mappings; other styles are addressed through the native style tree, not merely unlocked buttons.
- Standard and grain styles can have identical names but different UUIDs. Requests use the exact installed source path. Existing applied entries are matched to installed RNI names; where source-tree checks cannot distinguish a version, the internal `rni-name:` token means **name-recognized RNI, not UUID-proven**. Capture One's lazily created source checkboxes can initially be Off even while that style is applied.
- **Clear RNI** removes each uniquely located, name-recognized RNI row through its native Clear from Background action and verifies the remaining style/preset list. It never invokes reset-all. A list without recognized RNI is a no-op; duplicate applied rows that cannot be uniquely targeted stop the operation. Clearing that exact native row does not require guessing its standard/grain UUID.
- **Single and multiple selection entry points.** The batch adapter temporarily switches to primary-only editing, enumerates the selected primary identities through native First/Next navigation, then runs the single-photo workflow for each item. It does not send one toggle to a heterogeneous selection. Successful completion restores the initial primary photo and editing mode.
- Connect a supported catalog once by confirming its complete path. Selecting different photos within it does not require reconnection. Each operation locks and revalidates its own target identities.
- Before any style change, read the live native state. An independently recognized current target can be a no-op; when the existing version is unresolved, remove its exact RNI row and reapply the requested source path. Thus a repeated click leaves the requested effect present, but is not always a no-send operation. Read again after sending. A sent command is not reported as confirmed until the native result matches, and an uncertain command is never automatically retried.
- **No automatic tool-tab switching.** The visible Styles and Presets tool is read directly. Source-tree expansion can move its scroll position. Missing, hidden, collapsed, unreadable and genuinely empty lists are reported separately; the program does not interpret a reader failure as an empty list.
- Style observations cache the specific tool subtree rather than repeating whole-window scans. One UIA reader is allowed in flight; an eight-second timeout cannot cancel a blocked provider, so the old worker remains isolated and subsequent requests cannot pile up behind it.
- Cancel the connection, press Esc or close the palette to stop subsequent actions. A batch interruption reports its confirmed prefix and leaves the current primary in place for inspection; editing mode may remain primary-only.
- Favorites and window placement remain shared across upgrades in `%LOCALAPPDATA%\RniPalette`. Existing shared state is never replaced by a bundled seed. Old releases and their executables are not overwritten.

### Limits

The current configuration is specific to the owner's Windows Capture One installation and explicitly named Photography-Master work catalog / RNI-Panel-Sandbox. Startup is always preview-only; the user must confirm the actual catalog path before enabling application.

The viewer must expose one unique primary photo, including during a multi-selection. Ambiguous filenames, multiple variants of one image and comparison viewers cannot yet be identified safely. Application refuses unknown or mixed existing style stacks rather than silently replacing unrelated effects. Clear identifies RNI rows by exact installed display names and leaves unrelated rows alone; it does not claim that every initial RNI row's UUID is available. A locally created unrelated style with an identical RNI name cannot be distinguished from that name alone.

Clear-then-apply is a sequence of native GUI actions, not an atomic transaction. If application fails after removal, the old RNI may already be gone; the failure must be inspected and is never silently retried. Post-application confirmation combines this operation's exact requested source path with the native applied-name readback. It is not a claim that the applied-list control exposes a UUID, nor a permanent identity cache across later native edits.

The native Styles replacement mode must be enabled for application and metadata auto-sync must be disabled. The old `RNI Panel Demo` key set is no longer required for native-tree execution. The app does not silently edit Capture One configuration or install keyboard mappings.

The actual document selector, browser/viewer identity controls and Styles and Presets tool must remain available to Windows UI Automation. This is a real GUI dependency, not an official business API. A missing control is a reported limitation, not evidence that the user has no tool or no applied style. There is no automatic restoration of historical Lightroom edits or repeat of the completed photo migration.

### Calling architecture

1. Windows UI Automation reads the native document, selection, applied list and style-tree controls, and invokes supported native UI actions.
2. `SendInput` dispatches existing native style shortcuts when an applicable saved binding is available; otherwise the exact native style-tree checkbox path is used.
3. Windows `winsqlite3` opens the catalog read-only solely to disambiguate photo identity. It is not the authority for live style state, and there are no online database writes or original-file changes.

Capture One has a Windows developer SDK, but this project has not established a supported external API for the needed live selection / ApplyStyle / per-style-clear operations. In-process private methods and macOS AppleScript are not treated as Windows external interfaces. See [current architecture notes](docs/architecture-current.md).

## Build

Requires Windows with the installed .NET Framework 4.x C# compiler. No NuGet packages or installer are needed.

```powershell
.\build.ps1 -OutputDirectory .\build-local
.\build-local\RniPanel.Tests.exe .\test-results\local-run
```

Use a fresh test output directory for each run: tests intentionally refuse to overwrite an existing generated key set. The local integration tests expect the installed RNI collection. Optional catalog identity regression cases use an ignored `test-fixtures.local.json` file (or a second test-program argument); no personal filenames or catalog records are included in the repository. Unit tests do not send Capture One input.

## Implementation

- `PanelCore.cs`: full style index, favorites, existing shortcut metadata, identity policy, apply/clear workflows and primary-by-primary batch coordinator.
- `CatalogReader.cs`: Windows `winsqlite3` read-only identity lookup.
- `NativeBridge.cs`: scoped UIA observation, explicit UUID/name-confidence handling, exact-row removal, guarded native shortcut/tree dispatch and result readback.
- `NativeBatch.cs`: live Edit All Selected Variants state, primary navigation and the batch adapter; no persisted-setting guess for live edit mode.
- `App.cs`: Windows Forms palette, explicit catalog confirmation, cancellation, progress and local logs.

### Validation status

The user's accepted 0.4 application, intensity switching, repeated-click protection and favorites remain the historical baseline. The failed 0.5 run is not a passed release test.

- 0.6 source and test executable build successfully with the local .NET Framework compiler.
- 85 core checks pass, including the existing tests plus full-catalog path identity, selective clear, batch preflight and partial failure handling. Four additional optional local catalog-identity fixture checks passed earlier in this development round. These tests do not send Capture One input and do not establish live UI compatibility.
- Live single-photo validation on Capture One 16.7.8: empty list → Fuji Natura 1600 25% (outside the original eight shortcuts), switch to 50%, repeat 50% without sending, and cold-start clear through the exact native applied row all passed with native list confirmation. No tool-tab switching occurred. Initial cold discovery was about 12 seconds; cached switching about 4 seconds and repeated no-op about 1.6 seconds on this installation, not performance guarantees.
- Live batch validation: **pending completion by the single sandbox executor**. Only the confirmed sandbox and current selection are used for agent-driven photo tests; the work catalog and original media remain untouched.

Automatic catalog/selection connection remains a later improvement. No further routine test matrix or checksum campaign is required for this personal tool; necessary builds and actual-use feedback are the intended verification level.

## Development synchronization

Coherent, buildable changes are committed and pushed to this repository. Generated files, diagnostics, personal settings, media, catalogs, and proprietary assets remain local. No background filesystem-sync service is installed.
