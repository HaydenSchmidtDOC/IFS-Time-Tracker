# Changelog

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
