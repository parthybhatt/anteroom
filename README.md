# Anteroom

A Windows tray app that tells you when a Claude Code session is waiting on you.

Claude Code sessions block silently: a permission prompt in a terminal on another desktop looks
exactly like a session that is still working. Anteroom sits in the tray, stars itself when any
session needs you, and shows one tab per session with the question it is stuck on. Clicking the
tab brings that terminal back to the front.

## How it works

Three pieces:

| Piece | What it is | Where |
| --- | --- | --- |
| `anteroom-hook.exe` | Tiny shim Claude Code runs on every hook event | `src/Anteroom.Hook` |
| `Anteroom.exe` | Tray app: state, tabs, settings | `src/Anteroom.App` |
| `Anteroom.Shared` | The wire contract between them | `src/Anteroom.Shared` |

Claude fires a hook → the shim reads the JSON payload on stdin, resolves the terminal window, and
writes one line to a named pipe (`anteroom.ipc.v1`) → the tray app updates. If Anteroom is not
running, the shim exits 0 without a word, so it never disturbs a session.

### Working out which terminal a session lives in

The hook payload does not identify the terminal. The shim walks its own parent process chain
(shim → claude → shell → OpenConsole → WindowsTerminal / Code) and takes the first ancestor that
owns a visible top-level window. Clicking the tab foregrounds that window; if it is gone, Anteroom offers
the session back as `claude --resume <id>` in a new terminal.

For VS Code this focuses the *window*, not the specific integrated-terminal tab — VS Code exposes
no way to target one.

### Raising and clearing the star

Claude has no "notification cleared" hook, so the state machine infers it:

| Event | Effect |
| --- | --- |
| `SessionStart` | Creates the tab |
| `Notification` | **Raises** the star — permission request, or 60s idle (classified by Claude's notification type, falling back to the message text) |
| `Stop` | **Raises** the star — turn finished, awaiting your reply |
| `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `SubagentStop`, `PreCompact` | Clears the star: Claude is moving again |
| `SessionEnd` | Removes the tab |

Any event for an unknown session id also creates its tab, so sessions already running when
Anteroom starts appear on their next hook rather than staying invisible.

`Stop` fires on **every** turn. If a ping each time Claude finishes talking is too much, turn
`Stop` off in Advanced settings — the tabs keep working, you just only get starred for permission
requests and idle sessions.

### Answering permission prompts from the tabs

Off by default. When enabled, Anteroom gates the tools you tick: the `PreToolUse` hook holds the
call open, the tab shows the full `tool_input` with **Allow / Deny / Always allow**, and the shim
prints Claude's decision envelope on stdout:

```json
{"hookSpecificOutput": {"hookEventName": "PreToolUse",
  "permissionDecision": "allow", "permissionDecisionReason": "Allowed in Anteroom"}}
```

`allow` skips Claude's prompt entirely; `deny` blocks the call and tells Claude why; saying
nothing means "no decision", which is Claude's ordinary permission flow.

**Anteroom decides before Claude asks — it cannot answer a prompt already on screen.** Nothing can
inject a response into a TUI prompt Claude has drawn, so gating necessarily happens up front.

**It cannot tell whether your own rules would have auto-approved a call.** The payload carries the
tool, its input and `permission_mode`, but no pre-computed approval. So a gated tool is a decision
you have chosen to take in Anteroom instead of the terminal — which is why nothing is gated until
you tick it, and why ticking `Read` or `Grep` will mostly make you click Allow for calls that pass
silently today.

Safety properties, all verified:

- **Ungated tools cost one local round trip** (~100ms of process start, no visible latency) and are
  answered "ask" without ever appearing in a tab.
- **Unanswered calls fall back.** When the hold window expires, the shim emits nothing and Claude
  prompts as usual. Same if Anteroom is closed, crashed, or never started.
- **Anteroom's deadline always fires before Claude's.** The registered `PreToolUse` timeout is the
  hold window plus 15s, so a held call ends deliberately rather than being cut off. Change the hold
  time and the settings dialog reports *Needs updating* until you re-register.
- **Skipped where it would be noise:** `bypassPermissions`, `plan`, `auto` and `dontAsk`, plus
  `acceptEdits` for `Write`/`Edit`/`NotebookEdit`.
- **Dismiss releases.** Dismissing or clearing a held tab hands the call back to Claude's prompt
  rather than stranding a blocked hook.

*Always allow* remembers the exact tool and input (`Bash|npm test`) in Anteroom's own settings,
which is separate from Claude's permission rules. The count and a **Forget rules** button are in
Advanced settings.

### Session names

Hooks carry no session title, so the tab name is the cwd's folder name, and the subtitle is the
session's first user prompt, read out of the transcript JSONL.

## Build and run

Needs the .NET 7 SDK.

```powershell
dotnet build Anteroom.sln -c Release
```

```powershell
.\publish.ps1
```

`publish.ps1` puts `Anteroom.exe` and `anteroom-hook.exe` into `.\dist` (add `-SelfContained` to
bundle the runtime). Run `Anteroom.exe`, then **Advanced settings → Connect to Claude Code**.

The hook path written into Claude's settings is absolute, so if you move the folder, reconnect —
the settings dialog detects the drift and offers **Update**.

`Anteroom.exe --settings` opens the settings dialog straight away, for a shortcut or a quick
check without going through the tray menu. The dialog sizes itself to its content but never grows
past the work area of the monitor it opens on; past that it scrolls.

## Updating

Anteroom updates itself from this repo's GitHub releases.

- **Tray menu, Check for updates** asks straight away.
- **Advanced settings, Periodically check for updates** checks in the background, daily by default.
  A background check only raises a notification; installing always takes a click.

It picks the package matching the install it is running from. A self-contained install is only ever
offered the self-contained package, because handing it the small one would leave a build that
cannot start without the .NET Desktop Runtime. Which flavour you have is shown beside the GENERAL
heading in Advanced settings.

The swap happens in place, so the absolute anteroom-hook.exe path in `~/.claude/settings.json`
stays valid and your settings survive untouched.

How it works, given Windows will not let a running program overwrite its own files:

1. The asset is downloaded to `%LOCALAPPDATA%/Anteroom/updates` and checked before use: it must
   contain both binaries and no path that climbs out of the extraction folder.
2. A throwaway copy of the **currently running** build goes to `%LOCALAPPDATA%/Anteroom/applier`
   and is started with `--apply-update`. It has to be the current build rather than the downloaded
   one: a version predating this protocol would ignore the arguments and start as a second tray app.
3. That copy waits for Anteroom to exit, backs the install folder up to
   `%LOCALAPPDATA%/Anteroom/backups`, copies the new files over, and relaunches. A failed copy is
   rolled back from that backup.

Downloads are HTTPS-only and limited to GitHub hosts. Releases are not code-signed, so the checks
are: the asset is named by this repo's own release JSON, the zip must look like an Anteroom
package, and you confirm the version before anything downloads.

## Releasing

`.github/workflows/release.yml` is triggered by hand only: **Actions -> Release -> Run workflow**,
then type the version (`1.0.0`, or `v1.0.0` — the leading `v` is stripped). Nothing is tagged or
published until you press that button, and it defaults to creating a **draft** so you can look
before it goes out.

The workflow creates the tag itself from the commit you run it against, so there is no tag to push
first. It refuses to run if that tag already exists. Each release carries two packages:

| Package | For |
| --- | --- |
| `Anteroom-<version>-win-x64.zip` | Machines with the .NET 7 Desktop Runtime (~0.5 MB) |
| `Anteroom-<version>-win-x64-self-contained.zip` | No runtime needed (~157 MB) |

The version you type is stamped into both binaries, and anything that is not `Major.Minor.Patch`
fails the run early rather than halfway through. The workflow also refuses to publish a package
missing either binary, since Connect writes an absolute path to `anteroom-hook.exe` and the two
must ship together.

The **Run workflow** button only appears once this file is on the default branch — GitHub reads
`workflow_dispatch` from there, so push `main` before looking for it.

## What Connect actually writes

It merges entries into `~/.claude/settings.json`, one per enabled hook:

```json
{
  "hooks": {
    "Notification": [
      { "hooks": [ { "type": "command", "command": "\"C:\\...\\anteroom-hook.exe\" Notification", "timeout": 5 } ] }
    ]
  }
}
```

Guarantees:

- **Merged, never replaced.** Your other settings and other tools' hooks are untouched.
- **Backed up first**, to `%APPDATA%\Anteroom\backups\claude-settings-<timestamp>.json`.
- **Written atomically** via a temp file and swap, so an interrupted write cannot truncate it.
- **Fully reversible.** *Remove hooks* deletes only entries invoking `anteroom-hook`, and only
  removes a wrapper group if it held nothing else.
- **Explicitly confirmed.** Both Connect and Remove ask first.

## Settings

Stored in `%APPDATA%\Anteroom\settings.json`.

- **Display tabs** — master switch for the tab panel. With it off, Anteroom still stars the tray
  icon and shows a Windows toast instead.
- **Always on top** — disabled when tabs are off.
- **Position** — the four corners.
- **Display screen** — disabled on a single-monitor machine.
- **Notification sound** — Windows' own sounds, Chimes by default, with a preview button.
- **Claude hooks** — per-hook toggles. `SessionStart` and `SessionEnd` are required, since the tab
  list cannot exist without them.
- **Permission prompts** — the master switch for answering prompts from the tabs, the per-tool
  list of what gets held, and how long a held call waits. Off, and empty, by default.
- **Remove idle tabs after** — drops a session's tab once it has had no hook activity for that
  long, so tabs do not pile up when a terminal is closed without firing `SessionEnd`. The Claude
  session is unaffected and its tab returns on the next event; note a starred tab is swept as well.

## The tab panel

Attention-first: sessions that need you sit at the top with a colored bar (amber = permission,
blue = idle, green = turn complete); everything else collapses into a dimmed *idle sessions*
group. With nothing waiting, the panel shrinks to a slim handle. Drag the inner edge to resize
all tabs; the width persists.

The panel is a `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` window: clicking it never steals the caret
from the terminal you are typing in, and it never appears in Alt+Tab. It is positioned in device
pixels via `SetWindowPos` so it lands correctly on mixed-DPI multi-monitor setups.

## Troubleshooting

`%APPDATA%\Anteroom\anteroom.log` records every hook the app receives. Hooks fail silently by
design, so this is the way to tell whether Claude is actually calling the shim.

- **Nothing appears** — check the log for `hook …` lines. None means Claude is not calling the
  shim: confirm Advanced settings shows *Connected*, and note that hooks are read when a session
  starts, so open a new session after connecting.
- **Clicking a tab does nothing** — the terminal window is gone; Anteroom falls back to resume.
- **Tabs on the wrong monitor** — set *Display screen*, which is only enabled with 2+ screens.
