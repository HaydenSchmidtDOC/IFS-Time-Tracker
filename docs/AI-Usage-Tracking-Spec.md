# AI Usage Tracking - Spec

Status: draft. Records AI coding-assistant sessions against TimeTracker projects and reports spend per project.

## Goals and non-goals

Goals
- When a new AI session starts, ask which project it is for (default: the live timer's project).
- Store one record per session, keyed by provider session id, assigned to a project.
- Report cost per project (USD), with an "Unassigned" bucket that can be reassigned.
- Stay light: no new services, no telemetry stack, no polling loops.

Non-goals
- Exact billing. All figures are list-price estimates and can differ from the Azure or GitHub invoice.
- Azure Cost Management, GitHub billing API, OTel receivers.
- Per-message attribution. Attribution is per session.

## Findings this spec relies on

Verified on this machine
- Claude Code writes `~/.claude/projects/<cwd-slug>/<sessionId>.jsonl`. Assistant lines carry `message.model` and `message.usage` (input, output, cache read, cache creation). There is no cost field in the file.
- Copilot CLI and VS Code Copilot share `~/.copilot/session-state/<sessionId>/` (`events.jsonl`, `workspace.yaml`). VS Code sessions have `client_name: vscode-agent-host`, CLI sessions `github/cli`.
- Only the 3 newest of 8 Copilot sessions (Oct 2026) contain usage events. July sessions have none.
- Copilot events: `session.usage_checkpoint` (running `totalNanoAiu`, `totalPremiumRequests`), `session.shutdown` (totals, token details, `modelMetrics`), and in CLI sessions `session.usage_record` (per call, with `costPerBatch` per 1M tokens). Recomputing one record from tokens x `costPerBatch` matches `totalNanoAiu` exactly.
- Copilot data contains no USD values.
- Hook processes inherit Claude Code's environment (`ANTHROPIC_BASE_URL` is set in this session).

From Claude Code docs, not yet tested here
- `SessionStart` hook input: `session_id`, `transcript_path`, `cwd`, `source` (`startup|resume|clear|compact|fork`), optional `model`. Hooks can be `async`. Cannot block.
- `SessionEnd` has a 1.5 s default budget.
- Statusline command receives JSON incl. `session_id` and `cost.total_cost_usd` (client-side list-price estimate; resets on `/clear`).
- `CLAUDE_CODE_USE_FOUNDRY` and `ANTHROPIC_FOUNDRY_*` env vars identify Foundry. Unverified: no Foundry access on this machine.

## Architecture (all stages)

```
Claude SessionStart hook --(pipe)--> TimeTracker --> filter by endpoint --> picker --> ai_sessions store
Claude statusline script --(pipe)--> TimeTracker --> updates session cost
Copilot session-state watcher ------> TimeTracker --> picker --> ai_sessions store (stage 3)
```

Existing app facts used: WPF .NET 8, `TimeTracker.Core` holds models and JSON stores, data folder next to the exe (`AppPaths`), `Settings` JSON, `TrackerService.Active` is the live project, `SwitcherWindow` is the quick select, no IPC exists today, single-instance mutex in `App.xaml.cs`.

---

## Stage 1 - Claude Code capture and cost (no UI beyond minimum)

### 1.1 Transport
Open decision D1: named pipe (recommended) vs localhost HTTP vs drop-file + watcher.
- Named pipe `TimeTracker.AiSessions` (per-user). Server in the app, line-delimited JSON, one message per connection.
- Hook client: a small command that writes one JSON line to the pipe and exits. Must fail silently and fast (<200 ms) if the app is not running, so it never slows Claude Code.

### 1.2 Hook
Install in `~/.claude/settings.json` (user scope) a `SessionStart` entry with `"async": true`.
Payload sent to the app:
```json
{ "provider": "claude", "sessionId": "...", "cwd": "...", "transcriptPath": "...",
  "source": "startup", "endpointClass": "foundry", "baseUrlHost": "127.0.0.1" }
```
- The app offers a Settings button "Install Claude hook" that merges the entry into `settings.json` without touching other keys, and a matching "Remove". It makes a `.bak` first.

### 1.3 Endpoint classification and filter
Computed in the hook from its environment:
| Class | Rule |
|---|---|
| foundry | `CLAUDE_CODE_USE_FOUNDRY` truthy, or any `ANTHROPIC_FOUNDRY_*` set |
| local | `ANTHROPIC_BASE_URL` host is `127.0.0.1`, `localhost` or `::1` |
| anthropic | no base URL and no provider flags, or base URL host ends `anthropic.com` |
| other | anything else (Bedrock, Vertex, custom gateway) |

Check order: foundry, then local, then anthropic, then other.
Setting `AiTracking.EndpointsToTrack` (multi-select, default: foundry, anthropic, local). Sessions whose class is not selected are dropped with no prompt and no record.
Caveat: "local" means the address. A localhost proxy may front Foundry or Anthropic. Open decision D2: add an optional "upstream override" map (baseUrl host+port -> class) in settings so a known proxy can be classified as foundry.

### 1.4 Session lifecycle rules
- `startup`, `fork`: new session, prompt (stage 2). In stage 1, auto-assign to the live project, or Unassigned if no timer is running.
- `resume`: reuse the existing record if one exists for the session id; otherwise treat as new.
- `clear`, `compact`: `clear` produces a new session id, treat as new. `compact` keeps the id and does nothing.
- Duplicate pings for the same session id are idempotent.

### 1.5 Cost source
Primary: statusline script. Configure `statusLine` to run a script that reads the stdin JSON, sends `{sessionId, totalCostUsd, model, ts}` over the pipe, then prints the user's normal statusline text (empty by default). The app keeps the latest value per session.
- Open decision D3: the script must chain to any existing statusline command the user has. Settings stores that command and the script invokes it.
Fallback: transcript parser, used when no statusline value exists (headless `-p`, background sessions) or to backfill.
- Parse the session jsonl, de-duplicate assistant lines by `message.id` (keep the last, since streaming repeats it), sum usage per model, multiply by a bundled `pricing.json` (per-model input, output, cache read, cache write 5m, cache write 1h, USD per 1M tokens; editable, Foundry overrides allowed).
- Keep a byte offset per file so re-reads only parse appended lines.
Rule: statusline value wins when present. Mark each record's `costSource` (`statusline|transcript`) and show it in the UI.

### 1.6 Data model (Core)
New `ai_sessions.json` in the data folder (consistent with existing JSON stores):
```
AiSession { Provider, SessionId, ProjectId?, ProjectCode?, Cwd, EndpointClass,
            StartedUtc, LastSeenUtc, Model, CostUsd, CostSource,
            InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens,
            TranscriptPath, TranscriptOffset }
```
Writes go through the existing `JsonStore` atomic-write path. Costs are derived data and can be recomputed, assignments are the only user data.

### 1.7 Testing (xUnit in `tests\`, plus manual)
Unit
- Endpoint classifier: table-driven over env combinations incl. precedence.
- Transcript parser: fixture jsonl with duplicated message ids, multiple models, cache creation split, truncated last line. Assert totals and offset resume.
- Pricing: fixture price table gives the expected USD to 4 dp.
- Lifecycle rules: startup/resume/clear/fork/duplicate pings.
- Hook installer: merge into settings with existing hooks and unknown keys preserved, remove restores, `.bak` written.
Integration (manual, scripted where possible)
- Start a real Claude Code session with the hook installed and app running: record appears within 1 s of start.
- App not running: Claude Code starts with no delay or error.
- Compare statusline `total_cost_usd` against the transcript-parser figure for the same session. Record the delta. Expect close but not identical.
- Test `--resume` and `/clear`: confirm whether `total_cost_usd` resets or accumulates, and that session ids behave as assumed in 1.4.
- Confirm what the `SessionStart` payload and env contain on a `fork`.
- Foundry: when a Foundry session is available, confirm the env vars and model ids actually written to the jsonl, and check the price table keys match. Until then Foundry classification is untested.
Exit criteria: all unit tests pass, the delta test shows statusline vs parser within an agreed tolerance (proposal 5%), and all manual cases confirmed.

---

## Stage 2 - UI, popups, reporting

Defaults below are proposals. Open decisions are listed at the end.

### 2.1 New-session picker
- Reuse `SwitcherWindow` in a "pick for AI session" mode (D4). Same project list, same keyboard handling. Title shows provider, folder name from `cwd`, and endpoint class. The live project is pre-highlighted. Enter assigns and closes. Esc leaves the session Unassigned.
- Hotkeys are paused while open, as `OpenSwitcher` already does.
- Window is shown without activating (D5), so it cannot swallow keystrokes typed in the terminal. It still takes keyboard input once clicked or focused. Optional timeout (default off) that auto-assigns the live project.
- Queue: if several sessions start together, show one picker at a time, newest first, with a "+N more" hint. Never stack windows.
- If the app is minimised or only in the tray, the picker still appears (it is a small window, not the main window).
- Sessions the user dismissed stay Unassigned and can be reassigned later.

### 2.2 Settings
New "AI usage" section in `SettingsWindow`:
- Master enable switch.
- Endpoints to track (multi-select from 1.3), plus the upstream override map (D2).
- Install/Remove Claude hook buttons with status (installed, missing, stale path).
- Picker behaviour: focus, auto-assign timeout.
- Pricing file path and "open pricing.json".
- Chained statusline command (1.5).

### 2.3 Reporting (D6 - where)
Proposals, pick any:
- Timesheet window: an "AI cost" column per project for the selected period, total at bottom.
- New "AI usage" window: list of sessions (time, provider, folder, endpoint, model, cost, tokens, source) with a project dropdown to reassign, filters by period and provider, "Unassigned" filter.
- Export: AI cost column in the CSV and IFS export.
- Pill/tray tooltip: live session cost for the active project (tooltip is capped at 63 chars).

### 2.4 Edge cases
- Reassigning a session moves all its cost to the new project for every period it spans. Period attribution is by session start (D7: or split by message timestamps).
- Deleting or disabling a project: its sessions fall to Unassigned and prompt on next view.
- Data folder override respected for `ai_sessions.json`.
- Whats-new popup and CHANGELOG entry on release.

### 2.5 Testing
- ViewModel tests for the picker queue, default selection, and Esc behaviour.
- Reassign and total calculations per period.
- Manual: picker does not steal focus mid-typing, works with the window minimised, works with two sessions starting at once, hotkeys resume after close.

### 2.6 As built (v0.6.4)
- Picker: `SwitcherWindow(AiSession)`, `ShowActivated=false`, no close on deactivate, single click or Enter assigns, Esc leaves Unassigned. Sessions queue in `App.ShowNextAiPick`. Hotkeys are not paused (it takes no focus); Enter/Esc need a click first. Optional timeout not built.
- Reporting: cost column in the main window (today, grows the window only when enabled); timesheet Totals/Calendar/Project reserve 15% of each bar for sessions with a right-hand dollar axis (`AiChartMath.NiceAxis`). Calendar packs overlaps into lanes (`AssignLanes`). Sessions attribute to the local day of their start (D7: by start). Unassigned sessions show in Totals/Calendar but not the Project tab.
- Reassign: click a session segment, `AiAssignDialog` (styled like Add time).
- Settings: enable switch (installs/removes the hook and statusline, and re-installs at startup when on), endpoints to track, override map. The AI usage session list window exists but its Settings button is hidden. Hook buttons, pricing path, picker timeout and export columns not built.
- Enabled by default.

---

## Stage 3 - VS Code Copilot (and Copilot CLI) addition

### 3.1 Detection
`FileSystemWatcher` on `~/.copilot/session-state` (respect `COPILOT_HOME`). On a new GUID-named folder, wait briefly for `workspace.yaml`, read `id`, `cwd`, `client_name`, `created_at`.
- Provider values: `copilot-cli` (`github/cli`) and `copilot-vscode` (`vscode-agent-host`).
- Ignore folders with a non-GUID name (e.g. `...-backup-...`) and `.session-operation-locks`.
- Run a startup scan so sessions created while the app was closed are discovered (and prompted for, or queued as Unassigned).
- Open decision D8: VS Code `cwd` is usually the workspace folder, which often cannot separate projects (many sessions share one folder). The picker is therefore needed for Copilot too. The `cwd` can be used to remember a last-chosen project per folder as the default.
- Endpoint filter does not apply to Copilot (always GitHub). Per-provider enable switches replace it.

### 3.2 Cost reading
- Read `events.jsonl` incrementally from a byte offset.
- Credits: take the latest `session.shutdown` totals if present, else the latest `session.usage_checkpoint`. Checked: real sessions have at most one `session.shutdown` event (earlier "multiple shutdowns" was a text-match error). Sessions from July 2026 (older Copilot versions) have neither shutdown nor checkpoint events, so they have no cost data and show as "no usage data". `usage_checkpoint` appears once per user prompt in newer sessions, so take the last one. A resumed session may still write a new shutdown, so if more than one appears take the last (assumed cumulative, verify on a resumed session).
- Store: `totalNanoAiu` (display as AI credits = nanoAIU / 1e9), premium requests, model, token counts.
- Sessions without any shutdown or checkpoint (crash, still open) show the latest checkpoint and are flagged "in progress".

### 3.3 USD (D9)
GitHub writes no dollar value. Options, default is A:
- A (recommended): price the tokens with the same `pricing.json` used for Claude, since the underlying model is named (e.g. `claude-sonnet-5.5`). One dollar basis for both tools; labelled "list-price estimate".
- B: credits x a user-set USD-per-credit rate setting, to reconcile with GitHub's bill. Shown alongside credits. The rate is not discoverable from the logs.
Credits stay visible regardless. Model ids from Copilot may differ from Claude's (`claude-sonnet-5.5` vs `claude-sonnet-5`); the pricing file needs alias entries, and unknown models show cost as "unpriced" instead of zero.
Note the cache-read ratio in Copilot data (0.05x input) differs from the usual 0.1x, so Option A may overstate or understate vs the GitHub bill. Reconcile on a couple of real sessions before trusting totals.

### 3.4 UI
- Same picker and reporting from stage 2. Rows show provider, credits and USD.
- Settings: Copilot enable switch, credits-to-USD rate (if B), session-state path override.

### 3.5 Testing
- Fixture `events.jsonl` files from real sessions (sanitised): CLI with `usage_record`, VS Code with checkpoints and multiple shutdowns, crashed with no shutdown.
- Watcher: new folder, folder created before `workspace.yaml`, backup folder ignored, startup scan.
- Recompute check: tokens x `costPerBatch` equals `totalNanoAiu` on every fixture.
- Manual: start a VS Code Copilot session and confirm detection, the picker, and that the final cost matches `session.shutdown`.

---

## Open decisions
| # | Question | Proposed default |
|---|---|---|
| D1 | Hook transport | Named pipe |
| D2 | Proxy classification (localhost proxy fronting Foundry/Anthropic) | Optional override map |
| D3 | Existing statusline chaining | Chain to the configured command |
| D4 | Picker UI | Reuse SwitcherWindow in a mode |
| D5 | Steal focus | No, show without activating |
| D6 | Where cost is displayed | Timesheet column + AI usage window |
| D7 | Period attribution across midnight | By session start |
| D8 | Copilot folder default | Remember last project per cwd |
| D9 | Copilot USD basis | Option A (price tokens) |

## Risks
- Foundry env vars and model ids are untested until a Foundry session is available.
- Statusline cost semantics on resume are untested.
- All USD figures are estimates and may not match invoices.
- Hook and statusline edits touch a user-level Claude config, so they must be reversible and backed up.
- Copilot session-state is an internal format and can change between versions. Parse defensively and ignore unknown events.
