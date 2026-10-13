# IFS Time Tracker

A small Windows app for logging hours against work projects, in a shape that maps onto IFS
timesheets (project code + ASN number). It sits in the tray with an optional floating pill so the
current project is always visible, and it can record your Claude Code usage and cost per project.

## Install

1. Download `IFS-Time-Tracker.exe` from the
   [latest release](https://github.com/HaydenSchmidtDOC/IFS-Time-Tracker/releases/latest).
2. Put it in a folder you want to keep it in and run it. Nothing else to install; the .NET runtime
   is bundled (the file is about 70 MB).
3. Windows may show a SmartScreen warning because the exe is unsigned. Choose **More info → Run anyway**.
4. A short tour starts on first launch. Add a project (code, ASN, colour) and you're tracking.

Your data lives in a `data` folder created **next to the exe**, so keep the exe somewhere
permanent (and back that folder up, or keep it in a synced folder such as OneDrive). To move
to another machine, copy the exe and its `data` folder.

### Updates

The app checks GitHub Releases at most once a day and offers to update in place. You can also
use **Settings → Updates → Check now**. Each new version shows a short "What's new" the first time
it runs. Release history is in [CHANGELOG.md](CHANGELOG.md).

## Using it

- Select a project and press the round button (▶ start, ■ stop, ⇆ switch), or double-click a
  project to start it. Selecting a project never starts it by itself.
- **Ctrl+↑ / Ctrl+↓** works anywhere (even minimised) and opens the quick switcher: arrows or the
  mouse wheel move, **Enter** or a double-click confirms.
- The floating pill can be dragged, collapsed to a colour dot, and double-clicked to reopen the
  main window.
- If you step away, an idle prompt asks what to do with the time. A note prompt on stop is optional.

### Timesheets

Open the timesheets view from the main window. It has three views:

- **Totals**: each day's hours stacked by project.
- **Calendar**: your day laid out by time. Drag to add time, click a block to edit it, drag its
  body to move it, drag its edges to resize, and hover then click ✕ to delete.
- **Project**: one bar per project for the month.

### AI usage tracking (Claude Code)

On by default; turn it off in **Settings → AI usage**.

- When a Claude Code session starts, a small picker asks which project it belongs to (click or
  Enter to assign, Esc leaves it unassigned). It never steals focus from your terminal.
- The main window shows today's AI cost next to the hours for each project.
- In the timesheet, each bar reserves a narrow lane on the right for AI sessions, with a dollar
  axis on the right. Hover a session for details; click it to move it to another project.
- Which endpoints are tracked (Anthropic, Foundry, local, other) is configurable in Settings.

Enabling it adds a `SessionStart` hook and a cost statusline to Claude Code's
`~/.claude/settings.json` (a `.bak` copy is kept). Disabling removes them. Only Claude Code is
supported so far; VS Code / Copilot is planned.

### Exporting to IFS

The monthly log (`data/log-YYYY-MM.csv`) can be re-mapped to the columns IFS expects through
`IfsExportMapping` in `data/settings.json`: an ordered list of `{ Header, Field }` entries, where a
field is a log column or a fixed `=text` value. Fields: `Date, ProjectCode, Asn, ProjectName,
StartLocal, EndLocal, DurationSeconds, DurationHours, Notes`. Output goes to
`data/ifs-export-YYYY-MM.csv`. There is no export button in the UI yet.

## Your data

Everything is plain files in the `data` folder:

| File | Contents |
| --- | --- |
| `log-YYYY-MM.csv` | One row per tracked block, one file per month |
| `projects.json`, `settings.json`, `state.json` | Projects, preferences, the running session |
| `ai_sessions.json` | AI sessions and their costs |
| `ifs-export-YYYY-MM.csv` | Generated exports |

Nothing is sent anywhere except the update check against GitHub. See [SECURITY.md](SECURITY.md).

## Building from source

Requires the .NET 8 or newer SDK.

```
dotnet run --project src/App/TimeTracker.csproj      # run
dotnet test                                          # tests
dotnet publish src/App/TimeTracker.csproj -c Release -p:PublishSelfContained=true -o dist
```

The publish step produces the single self-contained `dist/IFS Time Tracker.exe`.

```
src/Core   tracking engine, storage, CSV, IFS export, AI usage (no UI)
src/App    WPF desktop app (net8.0-windows)
tests/     xunit tests for Core
docs/      design notes, e.g. AI-Usage-Tracking-Spec.md
```

`TrackerService` (`src/Core/TrackerService.cs`) is the single source of truth for
start/switch/stop; extend behaviour there rather than in UI code-behind.

### Releasing

1. Bump `<Version>` in `src/App/TimeTracker.csproj` and add a `CHANGELOG.md` entry.
2. Merge to `main`, push, then tag and push the tag: `git tag v0.6.5 && git push origin v0.6.5`.
3. `.github/workflows/release.yml` builds the exe and publishes it with a `latest.json`
   (version, URL, SHA256) that the in-app updater reads. It only publishes if the tag points at
   the head of `main`.

The updater downloads the new exe, verifies its SHA256, then starts a second copy of itself with
`--update` that waits for the running app to exit, swaps the exe, and relaunches (the running exe
is locked, so the swap can't happen in-process).
