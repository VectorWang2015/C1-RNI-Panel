# RNI Palette

A small, single-user Windows companion for Capture One: film search, favorites, and real RNI 25 / 50 / 75 / 100 percent style variants.

This is an independent helper, not an embedded Capture One plugin. Capture One and the RNI styles must already be installed and licensed. No vendor ICC profiles, styles, program files, catalog data, or photographs are included.

## Current version: 0.5

- **Clear RNI** removes the currently applied, supported RNI style through one native toggle; it never resets all adjustments. An empty applied list is a no-op, and unknown/mixed stacks are not cleared.
- **No automatic tab switching.** Add and expand Capture One's **Styles and Presets** tool in the **Library** tab once, keeping it visible. The palette reads that tool directly; if it is absent/collapsed, it prompts rather than switching tabs.
- Connect the current work catalog once, then apply to its **currently selected single photo**.
- No need to filter the browser down to `1/1`; `1/N` is supported when the viewer and globally unique catalog record agree.
- Each click fixes its own target for the entire operation. Changing photos during an operation, changing catalogs, a modal, or lost focus stops that operation.
- Read Capture One's live applied-style list before sending a native shortcut. Clicking the current strength does nothing, so it cannot toggle the style off.
- Read the native result after sending. Never automatically retry a style command whose result is uncertain.
- Search and favorite the installed collection. Only standard RAW Portra 160 and Portra 400 are currently connected, for eight working buttons; the default filter shows these connected films.
- Favorites and window placement are shared across upgrades in `%LOCALAPPDATA%\RniPalette`. A bundled favorites file is only imported when no shared state exists.
- No routine program, photograph, catalog, or style-file checksum scans.

### Limits

The current configuration is specific to the owner's Windows Capture One installation and explicitly named Photography-Master work catalog / RNI-Panel-Sandbox. Startup is always preview-only; the user must confirm the actual catalog path before enabling application.

Multiple selections, ambiguous filenames, multiple variants of one image, comparison viewers, unknown existing styles and mixed style stacks are deliberately unsupported. The helper never writes the catalog database itself or changes original image files. There is no automatic restoration of historical Lightroom edits.

Existing local Capture One prerequisites: `RNI Panel Demo` key set, native Styles replacement mode enabled, and metadata auto-sync disabled. The app does not silently install or change these settings.

For the one-time no-tab-switch layout, right-click within the Library tool tab, choose **Add Tool → Styles and Presets**, and keep the tool expanded. This is a native workspace customization, not a plugin installation. See [Capture One's tool instructions](https://support.captureone.com/hc/en-us/articles/360003159377-How-do-I-add-a-floating-tool).

## Build

Requires Windows with the installed .NET Framework 4.x C# compiler. No NuGet packages or installer are needed.

```powershell
.\build.ps1 -OutputDirectory .\build-local
.\build-local\RniPanel.Tests.exe .\test-results\local-run
```

Use a fresh test output directory for each run: tests intentionally refuse to overwrite an existing generated key set. The local integration tests expect the installed RNI collection. Optional catalog identity regression cases use an ignored `test-fixtures.local.json` file (or a second test-program argument); no personal filenames or catalog records are included in the repository. Unit tests do not send Capture One input.

## Implementation

- `PanelCore.cs`: index, favorites, shortcut metadata, target policy, and testable apply workflow.
- `CatalogReader.cs`: Windows `winsqlite3` read-only identity lookup.
- `NativeBridge.cs`: Windows UI Automation, guarded native shortcut dispatch, and live style readback.
- `App.cs`: Windows Forms palette and explicit catalog connection.

The user accepted 0.4 operation and favorites, including the earlier native readback / round-trip / repeated-click behavior. Version 0.5 adds clear and direct visible-tool readback; it is compiled and handed to the user for actual-use feedback, without another full regression campaign or agent-driven photo edits.

Auto-connect and batch application are deferred. Batch research confirmed that native shortcuts toggle off when all affected images already carry the style, while visible style checkmarks describe only the primary image. A future batch workflow must not reuse the single-photo state decision or assume a background context-menu item is an unconditional apply command.

## Development synchronization

Coherent, buildable changes are committed and pushed to this repository. Generated files, diagnostics, personal settings, media, catalogs, and proprietary assets remain local. No background filesystem-sync service is installed.
