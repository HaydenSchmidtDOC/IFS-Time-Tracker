# IFS Time Tracker

A small Windows desktop app for logging hours against work projects, in a shape that maps
onto IFS ERP timesheets (project code + ASN number). Runs quietly in the background with a
colour-tinted tray icon and an optional floating pill so the current project is visible at a
glance, even minimised.

## Layout

```
TimeTracker/
  src/
    Core/   TimeTracker.Core.csproj   — tracking engine, storage, CSV, IFS export (no UI)
    App/    TimeTracker.csproj        — WPF desktop app (net8.0-windows)
  tests/    TimeTracker.Tests.csproj  — xunit tests for Core
  data/                                — created next to the running exe: projects, state,
                                          settings, and one log-YYYY-MM.csv per month
  TimeTracker.slnx
```

`data/` lives next to the executable by default, which — because the app runs from inside
this OneDrive-synced folder — means it syncs automatically. A future phone app is expected to
read/write the same files (that shared format is the sync mechanism, not a network API).

## Requirements

Nothing to install to **run** the built app — the published `.exe` is self-contained.
To **build** it, you need the .NET 8 or 10 SDK (already present on this machine:
`dotnet --list-sdks`).

## Day-to-day use

- **Run**: `dotnet run --project src/App/TimeTracker.csproj`
- **Test**: `dotnet test` (or `dotnet test TimeTracker.slnx`)
- **Build everything**: `dotnet build TimeTracker.slnx`

### Controls

- Select a project in the list, then press the round button (▶ start / ■ stop / ⇆ switch) —
  or double-click a project to start it immediately. Selecting never auto-starts.
- **Ctrl+↑** / **Ctrl+↓** (global, works minimised) opens the quick switcher: ↑/↓ or the mouse
  wheel move the highlight, **Enter** or a **double-click** confirms.
- The floating pill can be dragged (with momentum — it drifts to a stop and settles to the
  nearest screen edge), collapsed to just the colour dot by clicking the dot, and
  double-clicked to reopen the main window. Its own button toggles start/stop.
- Settings (⚙ in the main window) covers idle-prompt threshold, the note prompt, pill
  visibility, project management, the data folder, and the IFS export mapping.

## Packaging (single-file, no install required on the target machine)

```
dotnet publish src/App/TimeTracker.csproj -c Release -p:PublishSelfContained=true -o dist
```

This produces one `dist/IFS Time Tracker.exe` (~70 MB, .NET runtime bundled) that runs with
nothing installed — verified by launching it directly outside the SDK build output. Copy that
one file wherever it needs to live; a `data/` folder is created alongside it on first run.

*(`-p:PublishSelfContained=true` is a project-local switch, not MSBuild's built-in
`PublishProfile` — that name is reserved for `.pubxml` files, so a different one is used here
to avoid a spurious "profile not found" warning.)*

### Releases & in-app updates

Releases are published to **GitHub Releases** (see `.github/workflows/release.yml`): pushing a
tag like `v0.6.0` builds the self-contained exe and uploads it alongside a `latest.json`
metadata asset (version + asset URL + SHA256). The app checks that metadata at most once a day
and offers to update in place when a newer version exists — no manual zip download needed.

To publish a release:

1. Bump `<Version>` in `src/App/TimeTracker.csproj` (and add a `CHANGELOG.md` entry).
2. Commit and push a matching tag: `git tag v0.6.0 && git push origin v0.6.0`.
3. The workflow builds and uploads the release; the app will detect it on its next daily check
   (or via Settings → Updates → "Check now").

The update flow: the app downloads the new exe to a temp file, verifies its SHA256, then
launches a second instance of itself with `--update` (see `App.TryHandleUpdateArgs`) which waits
for the running app to exit, swaps the exe, and relaunches. The running exe is locked while the
app is alive, so the swap is always done by that second instance.

## IFS export mapping

The internal monthly log (`data/log-YYYY-MM.csv`) is a rich superset: date, project code, ASN,
name, start/end timestamps, duration in seconds *and* in decimal hours (rounded to the nearest
0.1 h — i.e. nearest 6 minutes, not always rounded up), and an optional note.

"Export month for IFS" (Settings) re-maps that log to whatever columns IFS actually expects,
via `data/settings.json` → `IfsExportMapping`, an **ordered** list of `{ Header, Field }`
pairs — ordered because column order matters for a CSV import and a plain dictionary doesn't
guarantee enumeration order. `Field` is one of `Date, ProjectCode, Asn, ProjectName,
StartLocal, EndLocal, DurationSeconds, DurationHours, Notes`, or a literal via `=text` (e.g.
`=DOC` to hard-code a column to a fixed value). The Settings window edits this table directly,
so once a real IFS timesheet export is available, matching it exactly is a settings change —
no code change.

## Notes for later work

- The phone app is expected to read/write the same `data/` files (via the OneDrive folder)
  rather than talk to this app directly — no sync protocol has been designed beyond "same
  files, same folder."
- `TrackerService` (src/Core/TrackerService.cs) is the single source of truth for start/switch/
  stop and is fully unit-tested with an injectable clock — extend behaviour there, not in the
  UI code-behind.
