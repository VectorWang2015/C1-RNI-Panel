# RNI Palette

A small, single-user Windows companion for Capture One: film search, favorites, and real RNI 25 / 50 / 75 / 100 percent style variants.

This is an independent helper, not an embedded Capture One plugin. Capture One and the RNI styles must already be installed and licensed. No vendor ICC profiles, styles, program files, catalog data, or photographs are included.

## Current version: 0.6.2

### 0.6.2: imported copies replace the built-in native path

The user reproduced `style-path-unavailable` on 0.6.1. The version-name correction was necessary but insufficient: a live UIA inspection found that the requested leaves were absent from the built-in folder. Same-UUID copies existed in a custom favorites folder under `Styles50`. C1's library deduplicates by UUID and the loaded user copies appeared under **自定义样式 / User Styles**, not at the original built-in paths. All twelve local copies matched their original files' UUID and content. No files were moved or modified to resolve this.

The index now discovers user-library copies in arbitrary folders by an already-indexed RNI UUID. Each film keeps its original card/favorite ID and gains exact alternative source paths and their native/XML names. The bridge tries these user paths and then the original built-in path, retaining the actual resolved source name with its checkbox. This is not a `000_Favourites` or Portra whitelist and does not use fuzzy name matching. Restart the panel after importing/moving style files to refresh its index; C1 does not need restarting.

**Fresh live validation on 2026-09-20:** an opt-in test harness used the production UIA reader and `NativeBatchSession`, not fake controls. It re-confirmed the full sandbox path and selection, then:

- Resolved all twelve four-strength paths for Natura 1600, Portra 160 V.5 and Portra 400 V.2 in their user-library locations, plus a built-in-only Portra 160 V.4 path. Path resolution alone is not an application test.
- Operated on two distinct, initially style-empty selected sandbox primaries: each applied Portra 160 V.5 100%, switched to Portra 400 V.2 25%, and cleared RNI. Each operation was confirmed by the native applied list. Repeating 400 V.2 25% on the second photo sent no command.
- Kept the existing nineteen-photo selection; only those two primaries were modified. Both ended style-empty and the original primary/count/edit mode were restored. This was a targeted test of the production per-primary batch adapter, not a nineteen-photo full-batch UI-button test.

Build passed; 106 checks passed with the optional local read-only identity fixture (102 without it). Added focused tests cover arbitrary user folders/renamed copies, exact UUID matching, unchanged favorites/cards and idempotent discovery. No primary-catalog changes, online database writes, original-media edits, C1 restart or vendor-asset changes. Live logs remain local, not in Git.

`tests/NativeSmoke.cs` is an explicitly invoked integration harness, not bundled in the panel. It refuses other catalogs or a running panel, checks physical Esc and native target guards, and its `pair` mode requires two initially style-empty primaries. Compile it separately against the built `RniPanel.exe`; arguments are `paths|pair <new-log-path>`. On failure inspect the retained C1 scene; it does not attempt rollback or retry uncertain commands.

### 0.6.1: native filename / XML-name mapping (incomplete fix; superseded)

The user confirmed multi-photo Natura 1600 application and clearing, then reported `style-path-unavailable` for Portra 160 V.5 and Portra 400 V.2. C1's source tree displays the filename stem (`v5`, `v2`), while these files' XML names contain `V.5`, `V.2`. The old path builder incorrectly used XML Name for the source-tree leaf.

This is fixed for the whole installed collection, not a Portra/version whitelist. A read-only metadata check found 1,248 of 1,680 styles have differing spellings, including version dots, capitalization, HC, HP5 and RSX. Source paths, checkboxes and cached source controls now use the exact filename stem; applied-list identification, confirmation and clearing accept the two exact names belonging to that indexed file. There is no punctuation-stripping or cross-version fuzzy match. XML-based family IDs remain unchanged to preserve favorites; search accepts both spellings.

The hotfix compiles and passes 97 core checks, including all indexed paths/name aliases, the two reported films' four strengths and favorite-ID stability. Existing native diagnostics independently confirm the reported `v5`/`v2` labels. **The new hotfix has not been live-applied in C1 by the agent**; the user's accepted Natura flows are not repeated. The 0.6 live results below remain historical evidence, not a new 0.6.1 full regression.

### 0.6 foundation

The first requirement is a quick film-style entry point for Capture One: select photos, click a film strength, or clear the current RNI effect. Version 0.5 was not accepted as usable: its first visible-style observation timed out before any style command was sent. Version 0.6 addresses that reader and removes the eight-command execution limit. Implementation and live validation are recorded separately below.

- The full installed collection is indexed by UUID and exact native tree path. On the development installation this is 1,680 styles / 426 families. Existing native shortcuts are discovered from their saved UUID mappings; other styles are addressed through the native style tree, not merely unlocked buttons.
- Standard and grain styles can have identical names but different UUIDs. Requests use the exact installed source path. Existing applied entries are matched to installed RNI names; where source-tree checks cannot distinguish a version, the internal `rni-name:` token means **name-recognized RNI, not UUID-proven**. Capture One's lazily created source checkboxes can initially be Off even while that style is applied.
- **Clear RNI** removes each uniquely located, name-recognized RNI row through its native Clear from Background action and verifies the remaining style/preset list. It never invokes reset-all. A list without recognized RNI is a no-op; duplicate applied rows that cannot be uniquely targeted stop the operation. Clearing that exact native row does not require guessing its standard/grain UUID.
- **Single and multiple selection entry points.** The batch adapter temporarily switches to primary-only editing, enumerates the selected primary identities through native First/Next navigation, then runs the single-photo workflow for each item. It does not send one toggle to a heterogeneous selection. Successful completion restores the initial primary photo, editing mode and any viewer-mode change made by the request.
- Connect a supported catalog once by confirming its complete path and selected count. Connection itself changes neither photos nor the viewer and does not require resolving a primary-photo UUID. Selecting different photos within it does not require reconnection. Each operation locks and revalidates its own target identities.
- On an explicit multi-photo Apply/Clear request, an already-unique primary viewer is used unchanged. Only the specific multiple-viewer condition triggers preparation: verify the same catalog/count, read the actual native Multi View check, switch it off only if checked, then resolve the primary. Other identity/database errors never trigger a view change. Successful batch completion restores a viewer mode changed this way.
- Before any style change, read the live native state. An independently recognized current target can be a no-op; when the existing version is unresolved, remove its exact RNI row and reapply the requested source path. Thus a repeated click leaves the requested effect present, but is not always a no-send operation. Read again after sending. A sent command is not reported as confirmed until the native result matches, and an uncertain command is never automatically retried.
- **No automatic tool-tab switching.** The visible Styles and Presets tool is read directly. Source-tree expansion can move its scroll position. Missing, hidden, collapsed, unreadable and genuinely empty lists are reported separately; the program does not interpret a reader failure as an empty list.
- Style observations cache the specific tool subtree rather than repeating whole-window scans. Native menus are searched in the menu bar or same-process popup, using fresh visible rows; only top-level header references are cached, never mode values. One style worker is allowed in flight. Expiration is checked again after blocking validation and immediately before native dispatch, preventing an expired request from beginning a later write.
- A timeout cannot cancel a native provider call that has already started. Its eventual result is unknown until observed; the existing worker retains its gate and the program does not retry it. Cancel the connection, press Esc or close the palette to stop subsequent actions. A batch interruption reports its confirmed prefix and leaves the current primary in place for inspection; editing or viewer mode may remain primary-only.
- Favorites and window placement remain shared across upgrades in `%LOCALAPPDATA%\RniPalette`. Existing shared state is never replaced by a bundled seed. Old releases and their executables are not overwritten.

### Limits

The current configuration is specific to the owner's Windows Capture One installation and explicitly named Photography-Master work catalog / RNI-Panel-Sandbox. Startup is always preview-only; the user must confirm the actual catalog path before enabling application.

Every style change still requires one uniquely identified primary photo. Ordinary multi-view selections can be prepared through the native viewer-mode command; ambiguous filenames, multiple variants of one image and unsupported comparison/reference arrangements still stop the operation. Application refuses unknown or mixed existing style stacks rather than silently replacing unrelated effects. Clear identifies RNI rows by exact installed display names and leaves unrelated rows alone; it does not claim that every initial RNI row's UUID is available. A locally created unrelated style with an identical RNI name cannot be distinguished from that name alone.

Clear-then-apply is a sequence of native GUI actions, not an atomic transaction. If application fails after removal, the old RNI may already be gone; the failure must be inspected and is never silently retried. Post-application confirmation combines this operation's exact requested source path with the native applied-name readback. It is not a claim that the applied-list control exposes a UUID, nor a permanent identity cache across later native edits.

The native Styles replacement mode must be enabled for application and metadata auto-sync must be disabled. The old `RNI Panel Demo` key set is no longer required for native-tree execution. The app does not silently edit Capture One configuration or install keyboard mappings.

The actual document selector, browser/viewer identity controls and Styles and Presets tool must remain available to Windows UI Automation. This is a real GUI dependency, not an official business API. A missing control is a reported limitation, not evidence that the user has no tool or no applied style. There is no automatic restoration of historical Lightroom edits or repeat of the completed photo migration.

### Calling architecture

1. Windows UI Automation reads the native document, selection, applied list and style-tree controls, and invokes supported native UI actions.
2. `SendInput` dispatches existing native style shortcuts when an applicable saved binding is available; otherwise the exact native style-tree checkbox path is used.
3. Windows `winsqlite3` opens the catalog read-only solely to disambiguate photo identity. It is not the authority for live style state, and there are no online database writes or original-file changes.

When a native WinForms editing-mode menu does not expose UIA Toggle state, Windows MSAA reads the exact visible menu row's name, role and checked flags, including the returned child ID. This is still GUI accessibility, not a Capture One business API. Missing accessibility state is never replaced with a saved setting or assumed Off value. Physical clicks are used where C1's actual mouse handlers are required; merely returning from UIA Invoke is not treated as an applied change.

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
- `NativeViewer.cs`: explicit-request-only primary-view preparation and successful-batch restoration; connection remains observation-only.
- `App.cs`: Windows Forms palette, explicit catalog confirmation, cancellation, progress and local logs.

### Validation status

The user's accepted 0.4 application, intensity switching, repeated-click protection and favorites remain the historical baseline. The failed 0.5 run is not a passed release test.

- 0.6 source and test executable build successfully with the local .NET Framework compiler.
- 85 core checks pass, including the existing tests plus full-catalog path identity, selective clear, batch preflight and partial failure handling. Four additional optional local catalog-identity fixture checks passed earlier in this development round. These tests do not send Capture One input and do not establish live UI compatibility.
- Live single-photo validation on Capture One 16.7.8: empty list → Fuji Natura 1600 25% (outside the original eight shortcuts), switch to 50%, repeat 50% without sending, cold-start clear through the exact native applied row, and clear an already-empty photo all passed with native list confirmation. No tool-tab switching occurred. Initial cold discovery was about 12 seconds; cached switching about 4 seconds and repeated no-op about 1.6 seconds on this installation, not performance guarantees.

The bounded two-photo sandbox checks on 2026-09-20 produced these results:

| Flow | Native result | Observed total time | Status |
|---|---|---:|---|
| Both 50% → 25% | 2/2 confirmed; 2 sends; original primary/editing mode restored | 115.40 s | Passed |
| Repeat 25% | 2/2 confirmed; 0 sends; styles remained applied | 75.61 s | Passed |
| Clear RNI on both | 2/2 confirmed empty; 2 removals | 108.99 s | Passed |

Batch is functional but still slow; it is **not yet a fast bulk editor**. The observed times include per-photo navigation and repeated native identity checks. A subsequently compiled, narrower opened-menu lookup avoids a measured whole-window fallback, but those style timings were not re-run after that optimization; no unmeasured speedup is claimed.

- Automatic multi-view preparation/restoration passed end-to-end in the final candidate on 2026-09-20: a two-photo empty clear confirmed 2/2 with no style commands, temporarily changed multi-view to primary view, and restored multi-view. The same check started with Edit All Selected Variants enabled, verified primary-only editing during the batch, and restored the original enabled mode. The test operator then returned that preference to its pre-test disabled state. The earlier false final context-check error was corrected without relaxing photo-identity checks.

Only the freshly confirmed sandbox and selection were used for agent-driven photo tests; the work catalog and original media remained untouched. The full style collection has an execution path, not an exhaustive per-film live test matrix.

Automatic catalog/selection connection remains a later improvement. No further routine test matrix or checksum campaign is required for this personal tool; necessary builds and actual-use feedback are the intended verification level.

## Development synchronization

Coherent, buildable changes are committed and pushed to this repository. Generated files, diagnostics, personal settings, media, catalogs, and proprietary assets remain local. No background filesystem-sync service is installed.
