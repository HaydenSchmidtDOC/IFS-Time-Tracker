# Changelog

## [0.6.0] Beta — 2026-09-24

### New

- **In-app updates from GitHub Releases.** The app now checks GitHub Releases at most once a day
  (Settings → Updates, on by default) and offers to update in place when a newer version exists —
  no more manually downloading a zip and replacing the exe. The new exe is downloaded, its SHA256
  verified, and swapped in by a second instance of the app once the running one exits, then the
  app relaunches. A "Check now" button in Settings forces an immediate check, and a "Skip this
  version" option stops the app nagging about a specific release. Releases are published by
  pushing a `vX.Y.Z` tag (see `.github/workflows/release.yml`).

## [0.5.0] Beta — 2026-08-14

### New

- **Per-project enable/disable.** A disabled project drops out of the main window and quick
  switcher (so it stops cluttering the pick-a-project list) but keeps its history and stays
  editable from Settings — nothing is deleted.
- **Optional sub-second timer display**, with a dedicated monospace timer font (Cascadia Mono's
  dotted zero read oddly blown up at the main window's clock size).
- **"Minimise app only" setting** (Settings → Tracking, default on). Hides the main window's
  close (✕) button, leaving only minimise, so the app can't be closed by accident and then be
  hard to find again — a real problem for users who'd also turned off the pill and/or tray icon.
  Minimise always still works and always leaves a taskbar entry behind.
- Combined with **"start minimised"**, the app now launches minimised-to-taskbar instead of
  fully hidden with no taskbar entry at all — so there's always a click-to-restore path even
  with the pill and tray icon both off. (If "minimise app only" is off, "start minimised" still
  behaves as before: no window, tray/pill only.)

### Fixed

- **The main window and the Timesheets window could both end up open at once.** Double-clicking
  the pill or the taskbar icon while Timesheets was open used to also pop the main window up
  behind it instead of bringing Timesheets forward — and then the main window's own "Timesheets"
  button did nothing, since Timesheets was already (invisibly) open. Pill double-click/right-
  click, and the tray icon's double-click/"Open", now always bring forward whichever of the two
  is the current front surface; the two are never both on screen together.
- **Escape now closes the Settings window**, and closes the Timesheet settings popover if it's
  open.
- Relabelled the main window's "REC" badge to "LIVE", matching the project list's own badge for
  the same state.
- **Taskbar icon click didn't minimise the app when it was already focused** — clicking just
  re-activated it instead of toggling it away, unlike virtually every other Windows app.
  Root cause: `WindowStyle="None"` (used for the app's custom-drawn title bars) strips the
  `WS_SYSMENU`/`WS_MINIMIZEBOX` native style bits the taskbar checks before it'll act — restoring
  just those bits (`Interop/TaskbarMinimizeFix.cs`) fixes the click without bringing back any
  native chrome.
- **Switching between the main window and Timesheets could leave the wrong window with real OS
  focus** — whichever one you switched to was visibly on top but the OS still treated the other
  as active, so the first click/keypress went nowhere useful. `Activate()` plus a `Topmost`
  toggle trick (already used in `App.ShowMainWindow`) fixes the common case, but was still losing
  a race against a same-frame `Hide()` call on the window being switched away from; that step is
  now deferred via `Dispatcher.BeginInvoke(..., DispatcherPriority.ApplicationIdle)` so it always
  runs after the `Hide()` has settled instead of racing it.
- Fixed a taskbar-icon flicker when closing Timesheets back to the main window: the timesheet
  window was hiding itself *before* showing the main window, leaving a brief gap with no visible
  taskbar entry for the app at all — looked like the app had relaunched. Reordered to show-then-
  hide, matching how the open direction already worked.
- **The Timesheets window now gets native minimize/restore ("genie") animation, Aero Snap, and a
  live taskbar thumbnail**, matching every other normal Windows app. It was already using
  `WindowChrome` for its custom chrome, but `WindowStyle="None"` — Windows has disabled the
  minimize/restore animation for *any* `WindowStyle="None"` window since Vista, regardless of
  WindowChrome — silently killed all three. Fixed by keeping `WindowStyle="SingleBorderWindow"`
  (the default) underneath WindowChrome instead, which is what WindowChrome is actually meant to
  pair with. Two more tweaks were needed alongside it: `GlassFrameThickness="0"` (not the `-1`
  "whole window is extended frame" sentinel, which brought back a thin native grey border and
  added real DWM glass-compositing overhead visible as lag on the fast-ticking ms clock) plus
  `NonClientFrameEdges="None"` to keep that border gone with the flat value.
- **The main window was rendering clamped to a sliver (~160px) instead of its real 372px width.**
  Not a WindowChrome regression (that attempt had already been reverted) — on Windows 11 24H2,
  `WM_GETMINMAXINFO`'s default `ptMinTrackSize` for an `AllowsTransparency` + `WindowStyle="None"`
  window comes back well under its declared `Width`, and `ResizeMode="NoResize"` doesn't stop
  Windows applying that minimum to the underlying HWND — it only removes the resize handles.
  Since `SizeToContent="Height"` then measures/arranges within whatever width Windows actually
  granted, the whole window rendered clamped down to it. Fixed by handling `WM_GETMINMAXINFO`
  directly and overwriting `ptMinTrackSize` with the window's own width
  (`Interop/MinTrackSizeFix.cs`).
- **Double-clicking the pill while the main window was already open crashed with "Unable to find
  an entry point named 'GetCurrentThreadId' in DLL 'user32.dll'".** `ForceForeground.cs`'s
  `GetCurrentThreadId` P/Invoke was declared against the wrong DLL — that function lives in
  `kernel32.dll`, not `user32.dll` — so it only failed once this code path actually ran it.

### Known limitations

- **The project list's scroll boundary doesn't land cleanly on a row edge.** Capping
  `ProjectList`'s height at any value shorter than its content either clipped a partial row or
  (once that was fixed) left too little scrollable range to be usable — a couple of rows'
  overshoot bought only a fraction of a row's worth of scrolling. As a bandaid, `MaxHeight` is
  raised well past any realistic project count (2000), so the window just grows to fit every
  project and scrolling effectively never triggers. Needs a proper max-visible-rows design (cap
  by row count, not a pixel height) before re-enabling scrolling for real.

- **The main window still doesn't get native minimize/restore animation, Aero Snap, or a live
  taskbar thumbnail.** Migrating it off `AllowsTransparency` onto the same `WindowChrome` +
  `WindowStyle="SingleBorderWindow"` pattern as Timesheets was attempted for the same benefit, but
  — unlike Timesheets, which only needed the two tweaks above — it triggered a cascade of
  regressions (thin grey border, ms-clock lag, the focus race) that couldn't be pinned down and
  fixed blind. Reverted back to `AllowsTransparency` rather than keep guessing; the main window
  still snaps instantly on minimize/restore instead of animating. Worth revisiting with an actual
  interactive test pass rather than remote trial-and-error.

## [0.4.0] Beta — 2026-07-27

### New

- **Guided first-run tour.** A fresh install now offers a short, click-through tour — adding a
  project, tracking time, the quick switcher, and a full walkthrough of the Timesheets window
  (both views, merge, and drag-to-add/edit/resize/delete in Calendar view, mimed with an
  animated overlay rather than touching real data). Skipped it, or want to see it again? Replay
  it any time from Settings → Start tutorial.
- **Drag to add time in Calendar view.** Click-drag empty grid space to highlight a span
  (showing the snapped time range and, once tall enough, the duration), release to open the Add
  dialog pre-filled to it. Hovering empty space shows a small "+" cursor instead of the default
  arrow.
- **A note prompt when a live session auto-stops on collision**, instead of silently banking an
  empty note — same as a normal Stop.
- **Smarter add-time defaults and multi-block splitting.** A fresh Times-mode add now defaults
  to a 1-hour span (or snaps sooner to butt against whatever's next) instead of a fixed
  configured duration. An entry that cleanly spans one or more whole existing blocks now splits
  into separate entries around them instead of being rejected, with gaps shorter than the
  configured minimum absorbed rather than becoming their own sliver rows.
- Editing an existing block now also shows its note, editable.
- **Merge sessions is now a one-click toggle** pinned to the chart's bottom-right corner
  (instead of buried in the settings popover), and the Timesheets window now **remembers
  whichever view (Totals/Calendar) you last used** instead of a fixed configured default.
- A block's note now shows under its time range in Calendar view, once there's room.

### Fixed

- A Calendar block no longer visibly jumps/snaps right before its edit dialog opens when you
  click it — grid-snapping now only kicks in once a press has actually left click range, not on
  the first pixel of hardware jitter.
- The currently-recording block in Calendar view always shows its title now, even if the
  session started too small to fit one at first (previously it could stay unlabeled for its
  whole duration).
- Double-click or Enter on the project you're already tracking now stops it, matching what the
  button does — previously all three quietly did nothing.
- The floating pill no longer shows up in Alt-Tab.
- An idle discard's note now backfills onto every earlier split of that session when it's later
  stopped with a note, not just the final one.
- The quick switcher now closes before a note prompt pops up, not after, so it doesn't linger
  stacked underneath it.
- Fixed the bar view's stacked-segment height: a fixed per-segment gap was being added on top of
  each segment's proportional height rather than carved out of it, so a day split into many
  small segments visibly grew taller than an equal-hours day with fewer of them.
- The Timesheet settings popover no longer flickers open then immediately shut on the same click
  that was meant to open it.
- The once-a-second live-tick redraw in Calendar view no longer resets hover/cursor state on
  whatever the mouse happens to be sitting on — it patches the running block in place instead of
  rebuilding the whole canvas every second.

### Under the hood

- Merged the two calendar drag-snap settings (edge-resize, whole-block move) into one
  `DragSnapMinutes`; added `MinSplitBlockMinutes` for the new multi-block-split threshold; and
  dropped the separate "default view on open" setting now that the header toggle itself
  remembers the last-used view.
- Split live-session-collision handling into a peek/finish pair so the app layer can prompt for
  a note in between, the same shape a normal Stop already used.

### Known limitations

- **No undo yet.** Deleting or dragging a block is immediate — a bad drop or misclick means
  manually correcting it. The data folder lives in OneDrive by default, so its file version
  history is a partial safety net, but treat drag/delete carefully until in-app undo exists.
- A block can't be dragged across a month boundary (e.g. the last day of July onto the 1st of
  August) — that specific move is rejected/reverted rather than applied.

## [0.3.0] Beta — 2026-07-23

### New

- **"By amount" entries no longer need a clock time.** Adding time by amount now logs it as a
  plain day + project + duration total, with no specific start/end — so it never has to fit in
  whatever gap happens to be left in the day, and can never collide with anything. In Calendar
  view these show as small chips under the day header (click to edit) instead of a positioned
  block in the timed grid; Totals view needed no changes, since it was already duration-based.
- **Tracking now protects itself against overlapping an existing entry.** Starting or switching
  refuses (with an explanation) if "now" already falls inside a block you've already logged.
  While a session is running, it's also checked continuously — if it grows into the start of a
  block placed ahead of it, it stops right there instead of overlapping it, and tells you why.
- Times-mode add/edit and dragging a block can now target the future (previously restricted) —
  the two points above make that safe without needing that restriction.
- **A small "+" above every day's bar in Totals view**, riding with the bar as it grows (e.g.
  while a session is live), opening the Add dialog pre-set to that day.
- The version number is now shown at the bottom of the Settings window.

### Fixed

- Fixed a crash when closing the Add-time dialog (Cancel, Add, Escape, or the delete confirm) —
  the new click-off-to-dismiss behaviour below was re-entering `Close()` on a window already
  closing.
- The Add-time dialog now dismisses on an outside click, matching the Timesheet settings
  popover's light-dismiss feel.
- Reworked the Timesheets window's open/close animation after several rounds that each traded
  one visual bug for another (see the code comments on `TimesheetWindow.AnimateOpenFrom` for the
  full story). It's now a plain fade + subtle scale-settle on the chart area only — the header
  bar stays static/instant, since animating a toolbar-like strip read as broken chrome rather
  than smooth.

## [0.2.0] Beta — 2026-07-23

### New

- **Calendar view** — a 24-hour day view alongside the existing Totals (bar) view, switched
  with a header toggle. The day header stays pinned in place; scroll to zoom the hour scale
  (Ctrl+scroll) or navigate weeks (hover the date header, or Shift+scroll anywhere).
- **Add time manually** — the "+" button opens a dialog to log a block after the fact, either
  by amount (placed after the day's last block) or by exact start/end times. Double-clicking
  blank space in Calendar view opens the same dialog pre-filled to that day/time.
- **Edit existing blocks** — click any block in Calendar view to correct its project, start, or
  end time.
- **Delete blocks** — hover a block in Calendar view and click the small ✕ (confirms before
  deleting), or delete from within the edit dialog for blocks too small to show the hover
  control.
- **Drag to resize or move** — drag a block's edge to resize it (pushes a touching neighbour's
  edge back only when you actually drag into it, never when dragging away), or drag its body to
  move it — including past other blocks into an open gap, and onto a different day.
- **Live theme/accent updates** — switching Windows between light and dark mode, or changing
  your system accent colour, now applies immediately across the whole app. No restart needed.
- **ASN is now optional** when creating a project, and Code/Name/ASN can all be edited after
  creation — safe now that projects and time blocks carry stable internal IDs, so renaming a
  project no longer risks losing or misattributing its history.
- **Timesheet settings popover** — snap intervals, default view, chart grouping, and related
  settings are now consolidated into one live-applying popover on the Timesheet window (cog,
  bottom-right of the chart) instead of being split across two windows.
- 8-hour reference line on the bar chart; its Y axis no longer shrinks below a full workday.

### Fixed

- Adding or editing a block while a live session is running now correctly treats the rest of
  the day as unavailable (the session's true end isn't known yet), instead of allowing a
  double-booked/overlapping entry that would only surface once the session was stopped.
- Editing a block no longer falsely reports it as "overlapping an existing block" against
  itself.
- Week navigation by mouse wheel works in Calendar view (previously swallowed by the view's own
  vertical scrolling).
- The live session's "recording" pulse (border glow and dot) no longer flickers or snaps —
  it's a smooth, continuous breathing animation now, phase-locked across the once-a-second
  redraw instead of restarting from scratch each time.
- The Totals/Calendar toggle's slide animation now works in both directions (was silently
  snapping on one of the two transitions).
- The Totals/Calendar toggle (and similar switches) is a single click target now — clicking
  anywhere on it, including the already-active side, toggles it — and it now properly follows
  live system accent/theme changes instead of freezing at whatever they were when the window
  was first built.
- The "+" button no longer encroaches on the date range label in the Timesheet window header on
  narrower windows.

### Under the hood

- Projects and time blocks now carry stable internal IDs; the monthly CSV log gained two
  appended columns (`ProjectId`, `BlockId`) — old rows still read correctly via a deterministic
  fallback ID.

### Known limitations

- **No undo yet.** Deleting or dragging a block is immediate — a bad drop or misclick means
  manually correcting it. The data folder lives in OneDrive by default, so its file version
  history is a partial safety net, but treat drag/delete carefully until in-app undo exists.
- A block can't be dragged across a month boundary (e.g. the last day of July onto the 1st of
  August) — that specific move is rejected/reverted rather than applied.

## [0.1.1] and earlier

Initial tracking core, WPF app (main window, floating pill, quick switcher), weekly Timesheets
view, and packaging as a self-contained single-file executable. See commit history for detail.
