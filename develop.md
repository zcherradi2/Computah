# Computah: developer notes

Computah is a Windows desktop assistant. Press a hotkey anywhere and it captures what you are looking at. It sends that to a coding agent (Claude Code, or Qwen Code), streams the answer into a small floating chat bubble, and lets the agent act on the screen:

- point, frame and underline things
- draw on screen
- open files at the exact spot
- show its own edits with Undo
- with permission, use the mouse and keyboard

The idea behind it: **the screen is a shared imagination.** You should be able to get anything done by just talking, and when showing beats describing, the agent shows.

This file covers what has been built so far, how it fits together, and what is still open.

---

## 1. Build and run

| | |
|---|---|
| Stack | C# 13, .NET 10 (`net10.0-windows`), WPF + a little WinForms (tray icon, screens, folder picker) |
| Dependency | [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) 6.3.1.120 (MIT): code viewer in file and change cards |
| Build + restart | `.\build.ps1` stops Computah, runs `dotnet publish -c Release -o app`, then starts `app\Computah.exe` |
| Compile check only | `dotnet build Computah.csproj -c Release` |
| Icon | `tools\make-icon.ps1` draws `computah.ico` (dark wide screen, glowing blue eyes, own artwork) |
| Manifest | `app.manifest`: Per-Monitor-V2 DPI awareness |
| Single instance | named mutex `Computah_SingleInstance_v1` |

**Before restarting during development,** check that no session is mid-reply. Restarting kills the agent processes. A quick check: no session in `sessions.json` should have a user turn as its last entry.

### Where things live

| Path | What |
|---|---|
| `settings.json` (project root) | user settings, see below |
| `screenshots\` | full-size PNG of every capture |
| `%LOCALAPPDATA%\Computah\computah.db` | SQLite (`src/Db.cs`): sessions, bubble history, built-in agent turns, what each session saw of each window, dock position. Saves append rows; nothing is rewritten whole |
| `%LOCALAPPDATA%\Computah\images\{thread}\` | images of built-in conversations, one file each (the database stores the path) |
| `sessions.json.bak`, `threads.bak\` | the old JSON files, imported once into the database on first start and kept as a backup |
| `%LOCALAPPDATA%\Computah\commands.log` | every command the agent ran and every mouse/keyboard action, with session and permissions; rotates at 5 MB |
| `%LOCALAPPDATA%\Computah\workspace\` | the agent's working directory: outside any repo, away from the screenshots |
| `assist.log` | internal error log (see `Paths.LogFile`) |

### `settings.json`

```json
{
  "agent": "claude",                // which agent; chosen on first run
  "models": { "claude": "sonnet", "qwen": "" },
  "instruction": "...",              // default request when sending without typing
  "systemPrompt": "",                // extra instructions appended to Computah's own
  "control": "on",                   // mouse/keyboard: off | ask | on
  "showDock": true,                  // floating Computah button
  "attachClipboard": false,          // the clipboard is NOT sent by default
  "captureSelection": true,          // copy the selected text (Ctrl+C) when the hotkey is pressed
  "sendCursorCrop": true,            // second, full-resolution image around the pointer
  "screenDiff": true,                // element lists: send recently seen windows as changes only
  "loadMcpServers": false,           // false = --strict-mcp-config
  "allowedTools": ""                 // extra Claude --allowedTools rules
}
```

---

## 2. Using it

| Input | Action |
|---|---|
| **Ctrl+Alt+A** | capture into the **target session**, opens a compact input box |
| **Ctrl+Alt+A, A** (tap A twice while holding Ctrl+Alt) | capture and send right away with the default instruction |
| **Ctrl+Alt+A again** after releasing | hide the popup, re-capture, start over |
| **Alt+A** | capture into a **new session** |
| Floating button (bottom right, draggable) | click: sessions panel; drag a folder onto it: make it a workspace; drag a file onto it: share it with the next message |
| Sessions panel | click a session: make it the target; double-click: open; ✏ rename; 🗑 delete; chips for workspaces and playgrounds (right-click to manage); **+ folder** to add a workspace |
| Tray icon | new session, continue target, sessions, choose agent, mouse/keyboard control (off / ask / on), show floating button, screenshots folder, command log, settings, start with Windows, exit |

What a capture sends:
- a scaled screenshot of the monitor under the pointer, with a red pointer marker
- a full-resolution 1000×640 crop around the pointer
- the pointer position
- the focused app: process, title, path and PID
- the selected text

The clipboard is only sent if enabled. Attachment chips in the input box can be removed with Backspace, and removed items are really withheld: the agent is told they weren't shared.

---

## 3. Architecture

```
App.cs ─ tray, hotkeys, sessions, dock, panel, opening bubbles, playground recording
 ├─ Capture.cs ─ screenshot, crop, selection grab, focused-process info
 ├─ BubbleWindow ─ one conversation: input, chips, streaming text, cards, hints, actions
 │    ├─ AgentSession.cs ─ the agent process (stream-json), tool events, edit snapshots
 │    ├─ Hints.cs ─ tag filter + HintOverlay (point / underline / box / draw)
 │    ├─ Actions.cs ─ runs <action/> tags with SendInput (+ focus playground)
 │    └─ PeekWindow.cs ─ file / folder / change cards (AvalonEdit)
 ├─ DockWindow ─ floating button; DropFan (session tags while dragging); CommandFan (command ghosts)
 ├─ SessionsPanel ─ session list with workspace/playground chips
 └─ PickerWindow ─ first-run agent picker
```

### Files (`src\`)

| File | Responsibility |
|---|---|
| `App.cs` | `Program.Main`, `AssistHost`: tray menu, hotkeys (`RegisterHotKey` on a message-only window), chord/double-tap logic, `Trigger` capture flow, `OpenBubble` wiring, file drops, playground recording (`FillPlaygroundUrl`), sessions panel |
| `Native.cs` | P/Invoke: hotkeys, cursor, `SendInput` (click/wheel/Unicode typing/key combos), `ForceForeground`, no-activate and click-through window styles |
| `Settings.cs` | `Paths` (root, screenshots, workspace, log) and `Settings` load/save |
| `Capture.cs` | `CapturedContext` and `ContextCapture`: GDI screenshot, JPEG downscale (≤1568 px, ≤1.25 MP), crop, background PNG save, selection grab (waits for key release, skips terminals, restores the clipboard), `ContextText()` |
| `Agents.cs` | known agents (Claude Code, Qwen Code), detection, **command-line building** (see §4) |
| `AgentSession.cs` | one long-lived agent process per conversation (stream-json in and out); parses text deltas, tool calls (`ToolUsed`), tool results, edit snapshots (`FileChanged`), session id, permission mode; writes to the command log |
| `Prompts.cs` | Computah's instructions to the agent (see §5) |
| `Hints.cs` | `HintTag`; `HintFilter` (streaming, quote-aware tag extraction for `hint`, `action`, `request-control`, `request-access`); `ScreenMap` (image coordinates → screen); `HintOverlay` (click-through topmost window per monitor) |
| `Actions.cs` | `ActionRunner`: click/move/type/key/scroll/drag/wait/look/focus. Glides the cursor; stops on Esc or user mouse movement; holds back aimed actions without a current screenshot |
| `BubbleWindow.xaml(.cs)` | the chat bubble: compact input with chips, streaming text, setup strip (workspace/playground chips), permission/approval/access/workspace cards, hint and card routing, action loop, undo/redo news, "what's open on screen" note, focus-back after actions |
| `PeekWindow.cs` | file card, folder card, change card (diff + tabs + undo/redo), `DescribeOpen()` for the agent |
| `Diff.cs` | `FileChange` record; line diff (LCS with head/tail trimming) with changed-character spans |
| `SessionStore.cs` | `SessionRecord` (history, title, shared paths, `Workspaces`, `Playgrounds`), `Workspace`, `Playground`, `SessionStore` (loads from / saves to the database) |
| `Db.cs` | the SQLite connection (one, under a lock), schema, `Tx`, and the one-time import of `sessions.json` + `threads\*.json` |
| `ScreenMemory.cs` | per session and window: stable element numbers and change-only element lists (see "UI element map") |
| `SharedAccess.cs` | shared paths with an `edit:` prefix mean edits are allowed; helpers |
| `Playgrounds.cs` | playground creation from a capture, browser URL via UI Automation, `FocusAsync` (window + tab), `Describe()` for the agent |
| `Titles.cs` | one-shot title generation after the first exchange (`claude -p --model haiku --tools ""`) |
| `CommandLog.cs` | readable log of commands, results and mouse/keyboard actions, with permissions |
| `DockWindow.xaml(.cs)` | the floating button: drag to move, click for the panel, OLE drop target, glow while answering |
| `DropFan.cs` | while files are dragged over the button: target session (solid) and other sessions plus "New session" (ghosts) as drop zones |
| `DropTarget.cs` | COM `IDropTarget` (WPF drag-and-drop fails on no-activate windows); accepts file lists, paths, `file:///` URLs |
| `CommandFan.cs` | ghosts above the button showing the agent's last 5 commands; hover to expand, click to copy, right-click to dismiss |
| `SessionsPanel.xaml(.cs)` | session list, rename, delete, chips, add workspace |
| `PickerWindow.xaml(.cs)` | first run / "Choose agent…": detected agents with version and path |

---

## 4. How the agent is launched

One process per conversation, kept alive between messages (`src/Agents.cs`). Working directory: `%LOCALAPPDATA%\Computah\workspace`. `CLAUDECODE` and `CLAUDE_CODE_ENTRYPOINT` are removed from the environment.

```powershell
claude.exe -p `
  --input-format stream-json --output-format stream-json --verbose --include-partial-messages `
  --resume <claude-session-id> `                    # existing sessions only
  --model sonnet `
  --append-system-prompt "<Prompts.Build(settings)>" `
  --add-dir <each shared / workspace folder> `
  --allowedTools "Read(<shared file>)" "Edit(<edit-allowed path>/**)" <settings.allowedTools> `
  --strict-mcp-config `                             # unless loadMcpServers
  --disallowedTools "Read(<screenshots>/**)" "Edit(<read-only shared path>/**)"
```

- **`--append-system-prompt` is ignored on `--resume`,** which was verified. So on resume, Computah sends the full current instructions with the next message: "[Computah update: … replace any earlier ones]".
- **Restarts:** the process restarts within the same conversation when the control setting or the access paths change (`Dispatch` compares them).
- **Default permission mode:** read-only commands run; anything that changes things needs approval, and nobody can approve it, so it is refused. Edits only work through `Edit(...)` allow rules.
- **Qwen Code:** same protocol, no extra-directory flag; instructions go in the first message. The Qwen CLI was not logged in on this machine, so it is mostly untested.
- **Licensing:** running the unmodified CLI with your own subscription is documented use. Distributing Computah, or running it for other people on one subscription, is not allowed: they need their own login or an API key. See the Claude Code docs, `legal-and-compliance`, and the open question in §8.

### Built-in agent: "Computah (built-in)" (`src/BuiltinSession.cs`)

Computah's own harness, selectable next to Claude Code and Qwen Code. Both console agents remain options.

- **Model access:** calls Claude through the official Anthropic C# SDK (`Anthropic` NuGet 13.10.0) with the user's **API key**: `ANTHROPIC_API_KEY`, or an `ant auth login` profile.
- **Model:** default `claude-opus-5-5`, effort `medium`. Override with `models.computah` in `settings.json`.
- **The loop:**
  1. stream a model call; text goes to the bubble live, and `MessageContentAggregator` assembles the full message
  2. run the tool calls
  3. send back all results in one user message
  4. repeat, up to 40 steps per user message

  Thinking blocks are passed back unchanged. The instructions and the conversation are prompt-cached.
- **Tools:**
  - Anthropic's text editor tool `str_replace_based_edit_tool`: view, create, str_replace, insert
  - `glob` and `grep`, implemented in C#
  - `powershell`: read-only commands run directly; anything else shows a **Run / Don't run** card in the bubble
- **Permissions:**
  - read: shared paths and workspaces
  - edit: paths with edits allowed, plus Computah's own workspace folder
  - the screenshots folder is always denied
  - everything else returns "ask with `<request-access/>`"
- **Edits** raise `FileChanged` directly, so change cards and Undo work. File encoding and CRLF line endings are kept.
- **Conversations** are stored in the database (`thread_turns`, one row per turn, appended), with images as files under `images\{thread}\`. That is how they resume.
- **Pruning:** requests carry only the last 3 to 6 screenshots and the last 8 to 11 element lists; older ones become a one-line note. The cut moves in steps of 4 so the cached prefix stays the same between steps (`BuildMessages`).
- **Titles** come from one small `claude-haiku-5-5` call.
- **Shared code:** the bubble talks to both backends through `IAgentSession` (`src/AgentSession.cs`).

Not done yet for the built-in agent:
- server-side refusal fallbacks (needs the beta endpoint)
- web search and web fetch tools
- compaction for very long sessions
- turning Computah's on-screen abilities (hints, cards, actions) into real typed tools or an MCP server; they are still text tags
- not yet run against the live API on this machine, because no key is set

---

### UI element map (`src/UiElements.cs`)

Every capture also scans the focused window through Windows UI Automation (accessibility), in parallel with the screenshot.

- **What's scanned:**
  - the focused window, plus popups of the same app in front of it, such as open menus and dropdown lists
  - only visible buttons, links, fields (with their values), dropdowns, checkboxes, tabs (at most 8), menu items, list and tree items, rows, columns and texts
- **Limits:** at most 180 interactive items and 60 texts, deduplicated and numbered in reading order.
- **Speed:** about 80 to 190 ms on a large Chrome page.
- **What the agent sees:** a numbered list (`[48] button "Ajouter gamme" (1478,239)`) with each element's centre in Image 1, grouped by window (focused window, its popups, the taskbar), as its own text block.
- **Stable numbers** (`ScreenMemory`): each session remembers the elements it last saw per window. A new scan is matched against them (same type and name; nearest position when several match), so an element keeps its number for the whole conversation and new ones get new numbers. Kept in `window_views`, so it survives restarts.
- **Change-only lists** (`settings.screenDiff`, tray → Sight → "Send only what changed"): a window seen recently is sent as `same as in S12 except: + new / - gone / ~ changed or moved`, or `unchanged since S12`. Never chained: always against the last FULL list of that window, only while that list is among the last 8 (still in the agent's context), and only when at most half of it changed; otherwise the full list is sent and becomes the new base. With the setting off, every capture sends full lists (numbers still stable).
- **Caveat for Claude Code:** Computah can't see when Claude Code compacts its context, so a base list could in theory be summarised away within those 8 snapshots.
- **Targeting:** hints and actions take `element="N"`, and `ScreenMap.ResolveElement` turns it into exact coordinates: the centre for clicks and points, the box for frames, the bottom edge for underlines. `<action type="type" element="5" text="…"/>` clicks the field first.
- **Action loop:** a "look" waits up to 2.5 s for the list. Element numbers count as stale after a `focus` action, just like coordinates.
- **Why:** pixel coordinates are reliable only on models trained for computer use (Claude). The list lets weaker models such as DeepSeek click by name.
- **Limits:** things without accessibility info, such as bare icon buttons in some web apps, canvases and games, still need x/y.
- Not done yet: numbered marks drawn on the screenshot (set-of-marks).

### Sight modes (`settings.sight`, tray menu → Sight)

| Mode | Images sent | Element list |
|---|---|---|
| `perfect` (default) | whole screen (Image 1) + 1000×640 crop around the pointer | yes |
| `narrow` | only the 1000×640 full-resolution view, around the pointer | yes |
| `none` | no image | yes |

- **Coordinates:** x/y always mean "screen space", the scaled full-screen size, the same as the element list, in every mode.
- **In the narrow view:** the agent aims with `image="2"` or `element="N"`.
- **Moving the view:** `<action type="look" element="N"/>` or `<action type="look" x y/>` centres the next capture there (`ActionRunner.Result.LookAt` → `ContextCapture.CaptureAsync(settings, focus)`).
- **The screen chip** in the bubble shows the mode: "Screen (narrow)" / "Screen (elements only)".

## 5. What the agent is told (Prompts.cs)

1. **Identity and input:** what Computah sends (image 1 scaled, image 2 crop, app, pointer, selection), and that withheld items must not be fetched another way. Reply style: short, plain text.
2. **The screen is your imagination:** show rather than describe.
3. **On-screen hints:** `point`, `underline`, `box` (image coordinates, at most 3, only with a screenshot), and `draw` (SVG path data, default 10 s, `color` / `fill` / `stroke` / `seconds`, works without a screenshot).
4. **File and folder cards:** `<hint type="file" path line col endline endcol label/>`, `<hint type="folder" path select/>`. Only positions actually read; at most 2 per reply. Cards stay open and are reported back.
5. **Workspaces and playgrounds:** what they mean; propose a workspace with `<request-access mode="workspace"/>`.
6. **File access:** `<request-access path reason [mode="edit"]/>`. Changes show as change cards with Undo, so the agent should not repeat the code.
7. **Mouse and keyboard, depending on the `control` setting:**
   - **off:** `<request-control/>`, never try to control the computer with its own tools.
   - **on / ask:**
     - the mouse and keyboard are *one of* its main tools: use the apps instead of reinventing them
     - action tags: click, type, key, scroll, drag, wait, look, focus
     - only aim at current screenshots
     - small batches, then look
     - nothing destructive unless asked
8. **Extra instructions** from `settings.systemPrompt`.

Every message can also carry these Computah notes:
- session setup (workspaces + numbered playgrounds), when it changed
- open cards (path, highlight, lines in view, the user's selection, navigation, closed cards)
- undo/redo news
- withheld attachments
- results of actions (done, stopped, held back)
- the "screen after actions" screenshot

---

## 6. Features built (in order)

1. Hotkey capture → streaming bubble (smooth per-frame drain), follow-ups in the same session, close with Esc.
2. Focused process info (PID, name, title, path) sent to the agent.
3. Compact input with attachment chips (screen, selection, clipboard, pasted images, files); Backspace removes; Enter sends with or without text.
4. Hotkey semantics: Ctrl+Alt+A targets a session, Alt+A starts a new one, double tap sends immediately, a full re-press re-captures.
5. Withheld items really withheld; pointer position always known.
6. Renamed to **Computah**; own icon (wide dark screen, glowing eyes, no smile).
7. First-run agent picker (Claude Code / Qwen Code detection with version).
8. Floating button + sessions panel: target, continue, delete, open by double-click (no capture), position remembered, badge count, glow while answering.
9. Clipboard off by default; selection only.
10. Session titles generated after the first exchange; inline rename.
11. Hints: cartoon pointing hand (merged-outline glove, scale 0.9), glowing underline, glowing box; vanish after about 10 s or on hover.
12. Mouse/keyboard control with a permission flow (off / ask / on); restart with permission; action loop with automatic look (max 10 rounds); stop on Esc or mouse movement.
13. Drag files/folders onto the button (OLE drop target, no more "forbidden" cursor) → session tags fan out as drop zones.
14. Command ghosts above the button (max 5, hover expands, click copies, right-click dismisses).
15. Command log with session, tool, full command, result and permissions.
16. File and folder cards (AvalonEdit, syntax colors adapted to dark, glowing range highlight, copy/open/enlarge/close, folder navigation, images).
17. Agent knows what's open: cards, selection, lines in view, navigation, closed cards.
18. Access requests (read / edit / workspace) with Allow cards; shared paths are read-only unless edits are allowed.
19. Change cards: diff with removed (red, struck) and added (green) lines, changed characters framed, **one tab per edit**, per-tab **Undo/Redo** (Ctrl+Z / Ctrl+Y, byte-exact, ordered per file, refuses if the file changed since), agent told about undo/redo.
20. Chat refocuses after the agent's actions finish.
21. Drawing hints (SVG path, trace-in animation, colors, 10 s default, × close button).
22. Workspaces and playgrounds per session (chips in the bubble and panel; browser URL via UI Automation).
23. `focus` action: brings a playground window and **its exact browser tab** to the front.
24. Aimed actions without a current screenshot are held back, a fresh screenshot is taken, and the agent re-issues them.

---

## 7. Verified vs not verified

**Verified on this machine:**
- streaming
- resume
- hotkey double tap (6/6)
- withheld items
- Notepad typing via actions
- file access flags (read works, edit blocked unless allowed)
- the stream-json event order for edits
- file, folder and change cards, visually
- undo/redo order across tabs
- drawing render and × close
- command ghosts hover (no flicker)
- browser tab focus (Axiobat tab in the second of 4 Chrome windows, about 1 s)
- address-bar reading (about 0.2 s)

**Not verified / untested:**
- the drag fan visuals and dropping on a ghost session tag (simulated drags hung)
- "Ask me each time" control mode end to end
- `scroll` and `drag` actions; stop-on-mouse-move in practice
- the held-back-actions loop in a real conversation (built after the Slack incident)
- Qwen Code (not logged in here)
- multi-monitor with mixed DPI; hints and cards on a second monitor
- the agent actually producing `request-access mode="workspace"` and `focus` in real sessions (only CLI spot checks)
- very large files in cards; non-UTF-8 encodings in Undo (bytes are restored exactly, but the diff decoding assumes UTF-8/BOM)

---

## 8. Known issues and limitations

- **Licensing grey area:** Anthropic's consumer terms forbid "automated or non-human means… script" except via API key. The Claude Code docs allow the unmodified CLI with your own login. Fine for personal use as far as documented; ask Anthropic before sharing.
- **Playground identity:**
  - browser tabs are matched by title; two tabs with the same title can be confused
  - the stored address is only used for display and de-duplication
  - window handles change after a window is reopened (it falls back to the title)
  - other virtual desktops are not handled
- **Address-bar reading** fails when the bar contains typed text; it then falls back to the tab title.
- **"Closed cards"** news is global: it goes to whichever session sends next.
- **Shell commands that change files are always refused** (default permission mode). Only Edit/Write in allowed paths work. Deliberate, but limiting for things like `npm install` or `git`.
- **Change detection** only covers the agent's file tools (Edit, Write, MultiEdit, NotebookEdit), not changes made by shell commands.
- **Mouse/keyboard safety** relies on prompt rules plus stop-on-Esc/mouse-move. No allow-list of apps, no confirmation before sending messages or submitting forms (the agent sent a Slack DM on request; fine there, but risky in general).
- **The agent may click from memory:** now held back when there is no current screenshot, but a stale screenshot from a few turns ago is still trusted.
- **Restarting Computah** kills in-flight agent turns (`build.ps1` does this).
- **Edit permission storage:** the `edit:` prefix in `SharedPaths` is a compact but implicit format.
- **The sessions panel closes when it loses focus;** the folder picker is special-cased.

---

## 9. To do

### Next
- [ ] Test the drag fan and drop-on-session for real; fix visuals.
- [ ] Test "Ask each time", `scroll` / `drag`, and the held-back loop in real use.
- [ ] Remember playgrounds by **URL** and match tabs by URL when the title changed (read each tab's URL via UI Automation, or use the address bar after switching).
- [ ] Show playground status (open / closed / minimized) in chips; click a playground chip to focus it.
- [ ] Optional confirmation for "outward" actions (sending messages, submitting forms, purchases) even in `on` mode, or a per-app allow list.
- [ ] Pointing hints and drawings: report the live ones to the agent like cards.
- [ ] Per-session "closed cards" instead of global.

### Product
- [ ] Settings window (agent, model, control, clipboard, hotkeys, start with Windows) instead of editing JSON.
- [ ] Configurable hotkeys.
- [ ] Installer / single-file publish; auto-update.
- [ ] Bring-your-own-agent stays the model for sharing: each user signs in to their own Claude Code, or supplies an API key (Agent SDK). Don't use "Claude Code" in the product name ("Powered by Claude" is fine).
- [ ] Onboarding: a short tour of hints, cards, workspaces and control.
- [ ] Voice input (talk instead of type), in line with "do anything by just speaking".
- [ ] README with screenshots and a short demo (draw a horse; fix a bug → change card → Undo).

### Engineering
- [ ] Split `BubbleWindow.xaml.cs` (cards, actions, hints, access) and `PeekWindow.cs` (file, folder, change modes) into smaller pieces.
- [ ] Replace the `edit:` prefix with a typed access list.
- [ ] Unit tests: `HintFilter` (split tags, quotes), `Diff`, `SharedAccess`, `Playground.SameAs`, prompt building.
- [ ] Handle non-UTF-8 files in change cards (detect the encoding from `BeforeBytes`).
- [ ] Multi-monitor / mixed-DPI pass for overlay, cards, fan, panel.
- [ ] Shell-made changes: optional snapshot or diff of workspace files before and after a turn.
- [ ] Qwen Code: log in and test; consider other agents (Codex CLI, Gemini CLI) through the same stream protocol where available.

---

## 10. Production readiness (assessment of 2026-11-19)

**Verdict:** a strong personal tool and a convincing alpha, but not ready to ship to other people yet. Fine for the author and a few trusted colleagues.

### What's solid
- **The core loop:** hotkey → capture → streamed answer → follow-ups → sessions. Heavily used and holding up.
- **Showing instead of telling:** pointing, frames, drawings, file cards, and change cards with Undo/Redo and tabs.
- **Clicking by element** (UI Automation), which makes even weaker models such as DeepSeek click accurately.
- **Several agents:** Claude Code; the built-in agent on the Claude API or DeepSeek; Qwen Code once signed in. Every agent is checked before it can be chosen.
- **Tested end to end:**
  - DeepSeek through the bubble
  - clicking by element (Spotify on the taskbar, Notepad's menu)
  - the Narrow and None sight modes
  - file edits with change cards
  - resuming conversations

### What blocks a real release

1. **Safety of mouse and keyboard control (the biggest one).**
   - **Prompt injection:** anything on screen (web pages, emails, chat messages) reaches the agent as text in the element list or pixels in the screenshot. A malicious page could say "click Send" or "type this into the terminal". With control on, only the agent's judgement stops it.
   - **No guard on outward actions:** nothing asks for confirmation before sending messages, submitting forms or paying. The agent sent a Slack DM on request; nothing would have stopped it if a page had asked instead.
   - **Needed:**
     - per-app permissions (allow list)
     - mandatory confirmation for sending, submitting, paying and typing into terminals
     - treating screen text and element names as untrusted data in the prompt
2. **Privacy.**
   - **What leaves the machine:** every capture sends a screenshot and the window's text to a third party (Anthropic or DeepSeek), including whatever is visible: passwords, emails, customer data.
   - **What builds up locally:**
     - screenshots pile up in `screenshots\` forever
     - `commands.log` keeps command outputs
     - conversations (`threads\`, `sessions.json`) are stored unencrypted
   - **Needed:**
     - a privacy notice
     - automatic screenshot cleanup and retention limits
     - excluded apps (password managers, banking), where nothing is captured
     - a visible "what will be sent" step
3. **Untested areas.**
   - the built-in agent on Claude (no Anthropic key on the dev machine)
   - Qwen Code
   - multiple monitors and mixed display scaling
   - the drag-to-session fan and dropping on a ghost tag
   - "Ask each time" control mode
   - `scroll` and `drag` actions
   - the hover pause on cards; a real click keeping a card open
   - a narrow-sight view moved during a long task
   - **no automated test suite:** everything was tested by hand or with temporary `// PREVIEW-TEMP` hooks
4. **Robustness.**
   - **Fragile spots:**
     - the `edit:` prefix in `SharedPaths`
     - two very large files, `BubbleWindow.xaml.cs` and `PeekWindow.cs`
     - several features configured only through `settings.json`
   - **Error handling** covers the paths we hit, not every failure.
   - **Edge cases:**
     - switching windows during an action
     - a slow or offline API
     - huge files in cards
     - very busy accessibility trees
5. **Product basics.**
   - installer with **code signing**: unsigned apps that send keystrokes trip antivirus and SmartScreen
   - auto-update
   - settings window instead of JSON
   - configurable hotkeys
   - onboarding
   - crash reporting
6. **Legal and licensing.**
   - **Logins:** distributed users need their own Claude Code login or API keys; one subscription can't power other people.
   - **Naming:** no "Claude Code" in the product name ("Powered by Claude" is fine).
   - **Where data goes:** DeepSeek sends screen data to China-based servers, which matters for business users and GDPR.
   - **The grey area:** see §8 for subscription and automated use.

### Before a private beta, in order
1. **Action safety:**
   - confirmation for sending, submitting, paying and typing into terminals
   - per-app allow list
   - screen text marked as untrusted
2. **Privacy:** screenshot retention limits, excluded apps, privacy notice.
3. **Settings window and a signed installer.**
4. **Small automated test suite:** `HintFilter`, `Diff`, `SharedAccess`, `ScreenMap.ResolveElement`, `BuiltinSession` tools, prompt building.
5. **Work through the untested list above**, especially multiple monitors and the built-in Claude agent.
