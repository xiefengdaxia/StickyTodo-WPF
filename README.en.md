# StickyTodo

[简体中文](README.md) | **English** | [日本語](README.ja.md) | [한국어](README.ko.md)

A Windows sticky-note / to-do app written from scratch: WPF + .NET 8 + SQLite. **Everything runs locally** — no server, no account, no telemetry, no update checks; **every user is a lifetime member by default with all features unlocked**. The UI supports **简体中文 / English / 日本語 / 한국어**, switchable anytime from Settings.

> It borrows the product shape of similar tools (multiple boards, one window per board, recurring tasks, calendar), but **all code, UI, copy and colors are original** — no decompiled artifacts, resource files or private APIs from any third-party program were copied.

| Note (92% background by default) | 55% background (desktop shows through) | Graphite night theme |
|---|---|---|
| ![Note](artifacts/board-default.png) | ![Translucent](artifacts/board-translucent.png) | ![Dark](artifacts/board-dark.png) |

Two of the ten themes are new:

| Apricot (light) | Espresso (dark) |
|---|---|
| ![Apricot](artifacts/board-apricot.png) | ![Espresso](artifacts/board-espresso.png) |

![App icon](artifacts/icon.png)

| Manager (borderless + sidebar) | Standalone Settings | Board card context menu |
|---|---|---|
| ![Manager](artifacts/manager.png) | ![Settings](artifacts/settings.png) | ![Board menu](artifacts/manager-menu.png) |

Hover actions (buttons are pinned to the far right and only appear when the pointer enters that row):

| To-do row: ✓ mark done + ✕ delete | Done row: ↺ revert + ✕ delete |
|---|---|
| ![Hover todo](artifacts/hover-todo.png) | ![Hover done](artifacts/hover-done.png) |

Locked board (content dims but stays visible, never a white screen): `artifacts/board-locked.png`

| To-do context menu | Submenu (with check marks) | Appearance menu | Menu on a dark theme |
|---|---|---|---|
| ![Menu](artifacts/menu-todo.png) | ![Submenu](artifacts/menu-submenu.png) | ![Appearance](artifacts/menu-appearance.png) | ![Dark menu](artifacts/menu-dark.png) |

Note / memo board / calendar board / reminder popup:

| To-do note | Memo board (auto-saved) | Calendar (lunar + holidays + almanac) | Reminder |
|---|---|---|---|
| ![Note](artifacts/board-work.png) | ![Memo](artifacts/board-memo.png) | ![Calendar](artifacts/calendar.png) | ![Reminder](artifacts/reminder.png) |

Token usage board and Pomodoro timer:

| Token usage (trend + details) | Pomodoro timer |
|---|---|
| ![Token](artifacts/token-board.png) | ![Pomodoro](artifacts/pomodoro.png) |

The almanac panel (click any day to see auspicious/inauspicious activities) and lunar markings:

![Almanac](artifacts/calendar-lunar.png)

To-do list with due dates, reminders and recurrence rules: `artifacts/board-work.png`

TODO / Done view switch:

| To-do view (unfinished) | Done view (with completion dates) | Inline editing (click a title to edit) |
|---|---|---|
| ![Todo view](artifacts/board-todo-view.png) | ![Done view](artifacts/board-done-view.png) | ![Inline edit](artifacts/board-inline-edit.png) |

The UI in four languages (board names are user data and stay as typed):

| English | 日本語 | 한국어 |
|---|---|---|
| ![EN](artifacts/i18n-en.png) | ![JA](artifacts/i18n-ja.png) | ![KO](artifacts/i18n-ko.png) |

The language can also be switched from Settings (Japanese shown below):

![Japanese settings](artifacts/i18n-settings-ja.png)

---

## 0. Download and run (portable)

Don't want to install the .NET runtime? Grab the portable build:

**[⬇ Download the latest StickyTodo-x.x.x-win-x64-portable.zip](https://github.com/xiefengdaxia/StickyTodo-WPF/releases/latest)**

Unzip and double-click `StickyTodo.exe` — **a single 70 MB exe with the .NET 8 runtime bundled, zero dependencies**.

- The archive ships with `portable.marker`, so data (`sticky.db` and logs) lives in a `data\` folder next to the exe — **copy the whole folder to carry all your data**, perfect for a USB stick
- Delete `portable.marker` to fall back to normal mode (data in `%LOCALAPPDATA%\StickyTodo`)
- SmartScreen may warn on first run (unsigned): click "More info" → "Run anyway"
- Requires Windows 10 1809 (x64) or later

Build it yourself:

```powershell
.\tools\publish-portable.ps1 -Version 1.0.0                    # package only
.\tools\publish-portable.ps1 -Version 1.0.0 -CreateRelease     # package and publish a GitHub Release
```

The script runs tests → single-file publish → adds the portable marker → zips → **smoke test** (launches a real process with `--portable-check` to confirm data really lands next to the exe) → optionally uploads the release.

---
## 1. Quick start

```powershell
dotnet build -c Debug                       # build
dotnet test                                 # unit tests (245)
```

```powershell
$exe = ".\src\StickyTodo.App\bin\Debug\net8.0-windows\StickyTodo.exe"

& $exe                                     # normal use
& $exe --lang en                           # force English UI (this run only, does not change settings)
& $exe --selftest                          # headless self-check: create DB + seed board, exit code 0
& $exe --demo                              # seed sample data and open two note windows (only seeds when the DB is empty)
& $exe --demo --shot D:\shots              # demo + render every window to PNG, then exit
& $exe --demo --reminder-demo --shot D:\shots   # also create a due reminder to verify the popup flow
& $exe --recurrence-demo                    # recurring to-do end-to-end (create rule -> check -> roll -> calendar expand), exit 0 = pass
& $exe --view-demo --shot D:\shots          # screenshot the to-do view and the done view to verify the switch
& $exe --settings-demo --shot D:\shots --tall   # screenshot the Settings page (--tall grows it so every section fits)
& $exe --skin-check                         # verify theme resources really sync after switching, exit 0 = pass
& $exe --menu-demo --shot D:\shots          # expand context menu / submenu / appearance menu and screenshot
& $exe --menu-demo --shot D:\shots --skin graphite   # menu colors on a dark theme
& $exe --inline-edit-demo                   # inline editing end-to-end (rename / empty-title guard / draft row), exit 0 = pass
& $exe --inline-edit-demo --shot D:\shots   # same, plus a shot of the first row in edit mode
& $exe --interaction-demo                   # drag reorder / drop on tag / clear tags / click blank to exit editing, exit 0 = pass
& $exe --tag-drag-demo                      # drop-on-tag full path (apply tag -> clear tag), exit 0 = pass
& $exe --hover-demo --shot D:\shots         # screenshot hover ✓ ✕ and hover ↺ on a done row
& $exe --chip-count-demo                    # tag counts: do chip counters follow add/remove?
& $exe --session-demo                       # session restore phase 1: open 3 boards and persist the session
& $exe --session-demo --phase2              # session restore phase 2: simulate restart, verify 3 windows and positions
& $exe --autostart-demo                     # autostart regression: write registry -> read back -> delete -> restore
& $exe --token-demo --shot D:\shots         # Token board regression (fake data source, no DB access)
& $exe --token-demo --live                  # Token board regression against the real local opencode DB
& $exe --calendar-demo --shot D:\shots      # calendar regression: lunar / solar terms / holidays / almanac / year-month dropdowns
& $exe --pomodoro-demo --shot D:\shots      # Pomodoro regression: full cycle, pause, skip, today's count
& $exe --motion-demo --shot D:\shots        # motion regression: entrance / pulse / check / delete / done, verified by reading properties
& $exe --i18n-demo --shot D:\shots          # i18n regression: switch all four languages and verify UI text
& $exe --portable-check                      # portable mode self-check: prints the data directory and verifies the DB location
& $exe --manager-menu-demo --shot D:\shots  # manager card context menu (verifies Rename and Delete are present)
& $exe --settings-reopen-demo              # open/close Settings 5 times to verify it does not crash (exit 0 = pass)
& $exe --window-opacity-demo --shot D:\shots  # window opacity 100/60/40% and read back Window.Opacity
& $exe --ui-probe                           # print on-screen coordinates of drag handles and rows (for tools/drag-test.ps1)
& $exe --skin graphite --opacity 0.6 --window-opacity 0.9   # override appearance at startup (also used for screenshot regressions)
```

`--shot` renders the window itself via `RenderTargetBitmap` instead of capturing the screen, so it is **unaffected by DPI scaling or window occlusion** and can run in CI as a UI regression (output lands in `artifacts/`).

Runtime file locations:

| Content | Path |
|---|---|
| Database | `%LOCALAPPDATA%\StickyTodo\sticky.db` |
| Logs | `%LOCALAPPDATA%\StickyTodo\logs\app-YYYYMMDD.log` |
| Portable / isolated data dir | Set the `STICKYTODO_DATA_DIR` environment variable (used by demos and CI) |

Delete the data directory to return to a factory-fresh state.

---

## 2. Solution structure

```
StickyTodo.sln
├─ src/StickyTodo.Core             Domain layer (depends only on Ical.Net; no UI/DB)
│   ├─ Entities/                   Board / BoardWindowState / TodoItem / TodoCompletion / Tag / Note
│   ├─ Abstractions/               ITodoRepository / ITokenStatsProvider
│   ├─ Localization/               AppLanguage / UiStrings (four-language strings) / Translator
│   ├─ Entitlements/               IEntitlementService + PermanentFree / FreeTier
│   ├─ Geometry/                   SnapRect / WindowSnapper (edge snapping)
│   ├─ Calendar/                   MonthGrid / CalendarProjection / LunarCalendar / SolarTerms / ChineseHolidays / Almanac / Festivals
│   ├─ Pomodoro/                   PomodoroEngine (pure state machine, unit-tested)
│   ├─ Recurrence/                 RecurrenceEngine (RFC 5545 RRULE)
│   └─ Services/                   TodoService, AppSettingsService, TodoOrdering, DueDateText
├─ src/StickyTodo.Infrastructure   Infrastructure (SQLite / file system)
│   ├─ Persistence/SqliteTodoRepository.cs
│   ├─ Token/OpenCodeTokenStatsProvider.cs
│   └─ AppPaths.cs
├─ src/StickyTodo.App              WPF presentation (net8.0-windows)
│   ├─ App.xaml(.cs)               Minimal host + DI + Serilog + single instance + self-test/demo
│   ├─ Views/                      ManagerWindow / SettingsWindow / BoardWindow / MemoBoardWindow / CalendarWindow / TokenBoardWindow / PomodoroWindow / PromptWindow / ReminderPopup
│   ├─ ViewModels/                 CommunityToolkit.Mvvm (four board kinds + Settings + Pomodoro)
│   ├─ Controls/                   TrendChart (custom-drawn trend chart)
│   ├─ Services/                   WindowManager / TrayService / SkinService / LocalizationService / Motion / ScreenHelper
│   └─ Converters/                 includes localization converters (Loc / LocFormat / LocYear / LocMonth)
└─ tests/StickyTodo.Tests          xUnit (245 tests)
```

The icon generator lives in `tools/IconGen` (WPF vector drawing -> multi-size ICO):

```powershell
dotnet run --project tools/IconGen -c Release -- src/StickyTodo.App/Assets/stickytodo.ico
```

Dependency direction: `App -> Infrastructure -> Core`. Core knows nothing about the database, windows or DI.

---

## 3. Features

**Localization (4 languages)**
- The UI supports **简体中文 / English / 日本語 / 한국어**, switched from the Language dropdown in Settings and applied **immediately, no restart**
- Switching languages walks every open window and re-evaluates bindings that use converters (`LocalizationService.RefreshAllWindows`), so all text changes on the spot
- The choice is persisted (`appearance.language`) and restored on next start; it is applied **before any window is created** so the UI never flashes Chinese first
- `--lang en|ja|ko|zh-CN` forces a language for that run (screenshot regressions); it does not write back to settings
- **Lunar terminology stays in Chinese**: lunar dates, the sexagenary cycle, the twelve day-officers, solar-term names and the auspicious/inauspicious item lists are not translated (they are Chinese calendrical concepts with no meaningful translation), but the **labels** 「农历 / 节气 / 宜 / 忌」 follow the UI language
- Number units follow the language: Chinese uses 万/亿, other languages use M/B
- Consistency is enforced by unit tests: `LocalizationTests` checks that all four languages are present, that `{0}/{1}` placeholders match across languages, and that every translation survives `string.Format`

**Appearance (sticker feel)**
- **No system title bars anywhere**: the manager, Settings, note windows and prompt share a hand-drawn title bar (draggable; the manager and Settings also support edge resizing and maximizing)
- Note windows are **borderless, rounded, with a drop shadow** and 8px edge resizing (handled via `WM_NCHITTEST`)
- **10 themes**: Classic Yellow / Paper White / Mint / Sky Blue / Violet / Sakura Pink / Apricot / Graphite Night / Midnight Ink / Espresso (3 dark ones switch to light text automatically)
- **Two independent opacity controls, 10% steps (10 levels)**: background opacity 10–100% (only the paper becomes translucent, text stays sharp) and window opacity 10–100% (fades the text too)
- **Only notes, memo boards and the calendar are translucent**; the manager and Settings are opaque windows
- Hand-drawn controls: rounded checkboxes, slim sliders, flat scrollbars, segmented switch, cards, and a **context menu** (submenu arrows, check marks, separators, colors follow the theme)
- Title-bar buttons: 📌 always-on-top, 🔒 lock, 🎨 quick theme, ⋯ manager, ✕ close
- Notes do not occupy a taskbar slot (sticker style); access them from the tray or the manager

**Settings (standalone window)**
- **Language dropdown**: the four languages in their native names, applied immediately
- The 10 themes are shown as a **color-card grid** (two rows of five); one click applies everywhere instantly, no restart
- Two opacity sliders with live preview and instant saving
- A list of the three global hotkeys
- Data section: membership status, content statistics (boards / to-dos / done / tags), local storage path
- Entry points: the Settings button at the bottom left of the manager, and the Settings item in the tray menu

**Boards**
- One window per board, multiple can be open at once; opening an already-open board from the tray or manager activates the existing window
- **Four board kinds**: to-do board (list), memo board (one sheet of paper, auto-saved as you type), calendar board (month view), Token usage board
- Window position and size are remembered per board (restored on restart) and clamped back on screen when monitors change
- **Edge snapping**: snaps to screen edges and to nearby note windows (paper edges align, drop shadows excluded)
- Manager panel: create to-do / memo / calendar boards, rename, delete, double-click to open; shows kind and to-do counts
- **Card context menu**: Open / Rename… / Delete (the right-clicked card is selected automatically)
- **The whole window is draggable** (except buttons, list items, inputs and scrollbars); double-click any non-interactive area to maximize/restore
- Tray icon: checkable toggles for every board window, show/hide all, open the manager, quit; the X button only minimizes to tray

**Calendar board**
- Fixed 6-week month grid (Monday first); today highlighted, weekends in red, out-of-month days dimmed; weekday headers follow the language
- **Lunar date in every cell** (the 1st shows the month name, otherwise 初二 / 廿三)
- **Solar terms / traditional festivals / statutory holidays** are written into the cells: solar terms (立春, 清明, 霜降…), 除夕, 春节, 元宵, 端午, 七夕, 中秋, 重阳, 腊八, plus 元旦, 劳动节, 国庆 and so on
- **「休」/「班」 badges**: a red 休 means a holiday, a grey 班 means a make-up workday (data hand-entered from the State Council's 2024–2026 holiday notices; no network)
- Cells with items show a color dot plus a title summary; below is the item list for the selected day
- **Click any day to see the almanac**: lunar date, sexagenary year/zodiac, day stem-branch, the twelve day-officers, and five **auspicious / inauspicious** activities each
- **Year and month dropdowns**: jump to any year (60 back / 30 forward) and month, or navigate with ‹ › / Today; after switching, the selected day follows into that month
- Items can be checked off directly from the calendar, refreshing automatically
- **Drag an item onto another date cell to reschedule it** (keeps the time of day, changes only the date)
- Data comes from to-dos across **all boards**; recurring to-dos are expanded into multiple occurrences

**Token usage board**
- **Data comes from the local opencode database** (default `~/.local/share/opencode/opencode.db`, path configurable in Settings). **Fully offline**, opened read-only, never writes to someone else's DB
- Reads only `session_message` (v2); the legacy `message` (v1) table is fully migrated into v2 and no longer written, so it is ignored entirely
- **Switching ranges is fast**: each row parses the large JSON only once (multi-path `json_extract`), time filters are pushed into the innermost `WHERE`, and a single query computes totals, trend and per-model breakdown; historical days are cached in memory per day, only "today" is recomputed
- **Auto refresh every 5 / 15 / 30 seconds**, or click ⟳ to refresh manually
- **Ranges**: today (hourly) / last 3 days / last 7 days / last 30 days (anything beyond 7 days aggregates by day)
- Header figures: total tokens, call count, cache ratio; a second line breaks down input / output / reasoning / cache read
- **Trend chart**: stacked bars (input/output/reasoning/cache read/cache write) plus a line for call count; **click any bar or point to see that slot's details**, and auto refresh does not discard an open detail panel
- **By model**: token total, share, call count and breakdown for each model
- Failures only show in the footer, never as a modal; the last good data stays visible when offline

**Pomodoro timer**
- Opened from the tray menu; a small floating window, always-on-top by default
- 25 minutes focus + 5 minutes short break, **a 15-minute long break after every 4 pomodoros** (cycles configurable in `PomodoroEngine`)
- Ring progress plus a large countdown; **distinct colors for focus and break**, with the ring interpolated at 30fps
- Start / pause / resume / reset / skip; a tray notification when focus ends, and the next focus session starts automatically when a break ends
- **Today's completed count is persisted** and resets automatically across days

**To-dos (inline table editing)**
- **No "Add" button and no checkboxes**: a permanent draft row sits at the end of the list — "＋ type here, press Enter to add"
- **Action buttons appear only when the pointer enters a row**, pinned to the **far right end** so they never steal title space: `✓` (mark done) and `✕` (delete) on to-do rows; `↺` (revert) and `✕` on done rows
- **The badges and tag names on the second line align with the title's left edge**
- **Every row is directly editable**: click a title to edit, Enter commits and moves to the next row, Tab or clicking elsewhere also commits, Esc reverts
  - Empty titles are rejected, the original value is restored, and the row shows "Title cannot be empty"
  - The focused row highlights with an amber outline so you never edit the wrong row
- **Drag handle** (three dots on the left of each row): drag up/down to reorder; drop on a tag chip above to apply that tag; drop on «All / Uncategorized» to clear tags
- **Clicking blank space exits editing**: clicking anywhere outside an input commits and blurs the active row
- **TODO / Done view switch**: segmented buttons at the bottom (`To-do N` / `Done N`)
  - The to-do view shows unfinished items in your manual drag order
  - The Done view shows completed items **sorted by completion time, newest first**, each row labeled with when it was completed (today 14:41 / yesterday 09:05 / Oct 18 20:11 / Dec 30 2025 08:00)
  - The draft input is disabled in the Done view so you can't add something invisible
- **Due date (countdown)**: right-click to set today / tomorrow / this weekend / custom; rows show "N days left / due today / N days overdue", overdue in red
- **Reminders**: right-click to set in 30 minutes / in 1 hour / today at 18:00 / tomorrow at 09:00 / custom; when due, a bottom-right popup plus a tray balloon, with "Snooze 10 min"; polled every 20 seconds, entirely local
- **Recurring to-dos (RFC 5545 RRULE)**: right-click to pick daily / every weekday / weekly / biweekly / monthly / last day of month / yearly, or enter a custom RRULE (e.g. `FREQ=MONTHLY;BYDAY=2TU`)
  - Checking one does not delete it; instead it **records a completion and rolls the due date to the next occurrence** (reminders shift with it)
  - Rows show a 🔁 badge and a human-readable description
- **Drag reorder** (uses "move before item X" rather than an index, so dragging while a tag filter is active never misplaces anything)
- Context menu: toggle done, attach/detach tags, set due date, set reminder, set recurrence, delete

**Tags**
- Global tags; a to-do may carry several. Filter chips for All / Uncategorized / each tag, with counts
- Create, rename and delete tags; deleting a tag does not affect the to-dos themselves

**Global hotkeys** (work even when another window has focus)
- `Ctrl+Alt+N` new to-do (opens the default to-do board and focuses the input)
- `Ctrl+Alt+H` show / hide all notes
- `Ctrl+Alt+L` lock / unlock the active board
- If registration fails (taken by another program) it is skipped silently and startup is unaffected

**Motion (consistent immediate feedback)**
- **Window entrance**: fade-in plus a slight scale-up after the position settles (the animation targets the content, not the Window itself, so translucent windows are not bitmap-cached mid-animation, which would blur text)
- **Buttons**: the highlight layer fades in on hover (no hard color switch) and the button shrinks 4% then springs back on press — applies to `Primary`, `Ghost`, title-bar round buttons and tag chips
- **Checkboxes**: the box scales to 1.18 and bounces back while the check mark grows from 40% to 100% (`BackEase`); unchecking reverses it
- **Sliders**: the thumb scales to 1.2 on hover and 1.35 while dragging
- **To-do rows**: action buttons fade in on hover; `✓` pulses the row; `✕` fades the row out before it is actually deleted; a newly added row slides in from above
- **Tag chips**: bold and recolored when selected, with a hover highlight gradient
- **List cards** (manager): scale to 1.2% on hover
- **Calendar cells**: scale 4% and highlight on hover; the almanac panel fades in when a new day is clicked
- **Reminder popup**: slides in from below and fades in so it is hard to miss
- **Token board**: the refresh icon spins while fetching; the button dims only on the first load so switching ranges never flickers
- **Pomodoro**: the progress ring is interpolated at 30fps so it flows instead of jumping every 250ms
- **Respects the OS setting**: when Windows "Accessibility → Visual effects → Animation effects" is off, all motion is skipped (`Motion.Enabled` reads `SystemParameters.ClientAreaAnimation`)

**Other**
- Single instance (`Local\StickyTodo.SingleInstance`)
- Serilog rolling logs plus a global exception fallback
- Automatic database migration (`PRAGMA user_version`, v1→v4 implemented)

---

## 4. Data model

```sql
board(id, name, sort, is_default, kind, created_at,
      win_left, win_top, win_width, win_height)          -- kind: todo | memo | calendar | token
todo(id, board_id→board.id ON DELETE CASCADE, title, note,
     is_done, done_at, due_at, remind_at, reminded_at, recurrence,
     sort, created_at, updated_at)                        -- recurrence: RFC5545 RRULE
todo_completion(id, todo_id→todo.id, occurrence_at, completed_at)
tag(id, name UNIQUE COLLATE NOCASE, color, sort, created_at)
todo_tag(todo_id→todo.id, tag_id→tag.id, PRIMARY KEY(todo_id, tag_id))
note(id, board_id→board.id UNIQUE, content, created_at, updated_at)
setting(key PRIMARY KEY, value)
```

Design notes:

- **All timestamps are stored as Unix seconds (INTEGER)** and converted back with `.ToLocalTime()` when read (otherwise displayed reminder times are off by a time zone — a real bug that was hit and fixed); `DateTimeOffset` is only used at the repository boundary.
- **Logical foreign keys with cascade deletes**: every connection enables `Foreign Keys=True`, so deleting a board removes its to-dos and deleting a tag removes the associations.
- **WAL mode** (`journal_mode=WAL`, `synchronous=NORMAL`) for steadier concurrent reads across windows.
- **Migrations increment `PRAGMA user_version`** and `Initialize()` is idempotent; `SchemaMigrationTests` upgrades a real v1 database and verifies no data is lost.
- The repository uses short-lived pooled connections, never long-lived ones, to avoid races between windows and background threads.

---

## 5. Fully local (verifiable, not a slogan)

- The Token usage board reads **the local opencode SQLite database** — a local file read that also never goes online and sends nothing.
- There is no account system, no cloud sync, no telemetry and no update check.
- The host deliberately uses `new HostBuilder()` rather than `Host.CreateDefaultBuilder()`: the latter reads `appsettings.json`, environment variables and command-line configuration, whereas here all configuration sources are disabled and behavior is determined solely by code.
- An executable guarantee: `tests/StickyTodo.Tests/NoNetworkTests.cs` asserts that **the referenced-assembly lists of Core and Infrastructure contain none of** `System.Net.Http` / `System.Net.Sockets` / `System.Net.Requests` / `System.Net.WebClient`. If anyone reintroduces a network dependency into those layers, the test fails immediately.

---

## 6. Membership strategy (important)

The release build registers `PermanentFreeEntitlement`:

```csharp
// src/StickyTodo.App/App.xaml.cs
services.AddSingleton<IEntitlementService, PermanentFreeEntitlement>();
```

`PermanentFreeEntitlement` means every limit is `int.MaxValue`, `IsPro = true` and `Tier = Lifetime`, so:

- The membership badge, entitlement checks and quota validation in the UI **are all retained and fully functional** — the checks were not deleted;
- From the user's perspective it is permanently free with no feature gating;
- To charge in the future, swap in `FreeTierEntitlement` (already implemented: 2 boards / 3 tags / 5 recurrence rules / no ICS), or add an implementation that reads a tier from a local license file — **no business or UI code needs to change**.

Quota checks are only allowed in `TodoService`; the UI's only job is to surface `EntitlementLimitException`.

---

## 7. Done / roadmap

**M1 (done)**: layered skeleton, DI, logging, global exception handling, single instance, SQLite schema creation and migration, to-do CRUD, MVVM main panel, tray, entitlement abstraction.

**M2 (done)**
- [x] Multiple boards, one window per board, window position memory
- [x] Edge snapping (screen edges + adjacent notes), pure functions + 8 unit tests
- [x] Drag reorder (`TodoOrdering.MoveBefore` + 6 unit tests)
- [x] Tags and filtering (counts, right-click tag assignment, tag CRUD)
- [x] Dynamic tray board menu, manager panel
- [x] `--demo` / `--shot` demos and UI regression screenshots

**M2.5 Appearance (done)**
- [x] Borderless rounded note windows with shadow, hand-drawn title bar, edge resizing
- [x] Translucent background (10 levels, default 92%) with text always sharp
- [x] **Window opacity** (fades text too, 10 levels) adjustable **independently** of background opacity
- [x] **Manager and Settings also moved to borderless hand-drawn title bars**; the manager became a "left action rail + right board cards" layout
- [x] **Standalone Settings page**: color-card grid, dual opacity sliders, hotkey list, statistics and storage path
- [x] Themes expanded to **10** (added Apricot light and Espresso dark; the set is now 3 bright + 3 colored + 1 neutral + 3 dark). Brushes use `DynamicResource` so switching is instant and global; `--skin-check` validates contrast (body ≥4.5, accent ≥2.5) and resource sync for every theme
- [x] Consistent hand-drawn controls: rounded checkboxes, slim sliders, flat scrollbars, segmented switch, cards
- [x] **Hand-drawn context menu**: rounded popup, submenu arrows, check marks, separators, colors follow the theme (including dark)
- [x] **Truly opaque at 100%**: fully opaque backgrounds use alpha-free brushes (otherwise WPF still composites them as translucent layers)
- [x] **The whole manager window is draggable** plus card right-click (open/rename/delete)
- [x] **To-do drag handle** and dropping onto a tag chip to retag
- [x] **Clicking blank space exits edit mode**
- [x] **Inline action buttons replace checkboxes**: hover shows ✓ (done) / ✕ (delete), done rows show ↺ (revert)
- [x] **Slimmer title bar**: removed the count line, keeping only the board name and window buttons
- [x] **Drop-on-tag fix**: the `DragOver` effect differed from the drag source and cancelled the drop
- [x] **App icon**: sticky note with a folded corner and a check mark, 9 sizes (16–256) in one multi-size ICO, shared by windows and tray
- [x] **Hover buttons moved to the far right end**, second-line tags aligned with the title's left edge
- [x] **Locked white-screen fix**: with `IsEnabled=false` the default ListBox template painted a system-white background; replaced with a transparent template
- [x] **Manager and Settings are opaque windows**; opacity only affects boards; sliders in 10% steps
- [x] **Crash when toggling Settings fixed**: re-showing a closed singleton window throws; closing now only hides, plus a global exception fallback
- [x] Always-on-top / lock (remembered per board in the `setting` table)
- [x] Screenshot regressions with a fake desktop backdrop to verify opacity at a glance

**M3 first batch (done)**
- [x] Board kinds: to-do board / memo board (immersive writing, 800ms debounced autosave, character and save-time display)
- [x] Countdown: due date plus "N days left / N days overdue" inline badges
- [x] Reminders: local polling, bottom-right popup, tray balloon, 10-minute snooze
- [x] Global hotkeys: new to-do / show-hide all notes / lock the current board

**M3 second batch (done)**
- [x] Calendar board: 6-week month grid, day item list, drag to reschedule
- [x] Recurring to-dos based on iCalendar RRULE (`Ical.Net`): daily/weekly/monthly/yearly/weekday/nth weekday
- [x] Recurrence check semantics: records completion history and rolls the due date instead of disappearing
- [x] Human-readable recurrence descriptions, custom RRULE input and validation

**M4 to do**
- [ ] In-board search, filtering by multiple tags at once
- [ ] Excel import/export (NPOI) / ICS subscription
- [ ] Calendar week view / day view
- [ ] "Skip this occurrence" and "Edit a single occurrence" for recurrence rules

**Done (additional)**
- [x] TODO / Done view switch, Done view showing completion dates sorted newest first
- [x] **To-dos became inline table editing**: removed the add button and input, a permanent draft row at the end, every title directly editable (Enter moves down / Esc reverts / empty titles rejected)
- [x] **Compressed row height and tags pinned right**: one line holds handle / title / tag / row-end buttons, and the tag stays flush right regardless of title length
- [x] **Manager counts refresh live**: tag counts are recomputed on window activation (`Activated`) and board changes (`BoardsChanged`)
- [x] **Session restore** (records which boards were open and where): `BeginShutdown` is idempotent, fixing the WPF behavior where windows close before `OnExit` and the record got wiped
- [x] **Start with Windows** (registry `Run` + `--autostart`): restores only the previous boards without showing the manager, falling back to the manager when there is nothing to restore

**M5 (done)**
- [x] **Token usage board (reads the local opencode DB)**: 5/15/30 second auto refresh plus manual refresh, today / 3 / 7 / 30 day ranges, trend chart (stacked bars + line), click a bar for slot details, per-model shares
- [x] **Calendar board upgrade**: lunar dates, the 24 solar terms, traditional festivals, statutory holidays (休) and make-up workdays (班), almanac on click (stem-branch + twelve day-officers + auspicious/inauspicious), year and month dropdowns
- [x] **Pomodoro timer**: 25/5/15 minute phases, ring progress, start/pause/reset/skip, tray notifications, today's count persisted and reset across days
- [x] **Comprehensive motion**: window entrance, press scaling, checkbox bounce, row feedback, refresh spin, ring interpolation — all respecting the OS "reduce motion" setting

**M6 (done)**
- [x] **Four-language UI**: 简体中文 / English / 日本語 / 한국어, switchable live in Settings, with lunar terminology kept in Chinese; unit tests guard translation completeness and placeholder consistency
- [x] **Four README versions**: `README.md` (zh) / `.en.md` / `.ja.md` / `.ko.md`, cross-linked at the top

**M4 extension**
- [ ] ICS subscription and import/export
- [ ] Excel import/export (NPOI)
- [ ] More languages (optional)
- [ ] Portable packaging (single file / Velopack)

---

## 8. Known limitations

- There is no custom title-bar skin; the note windows use a hand-drawn bar while the manager and Settings also draw their own, so dragging and snapping depend on it — consider the feel before changing it.
- Snapping is computed from the window bounds, so it is approximate across displays with different DPI.
- Tag filtering is single-select chips; filtering by two tags at once is not implemented.
- The calendar is month view only; recurrence rules do not yet support "skip this occurrence" or "edit a single occurrence" (editing changes the whole rule).
- **Holiday and make-up-workday data is hand-entered** (covering 2024–2026). The State Council publishes the next year's schedule around November, at which point `ChineseHolidays` needs new data; years outside the range fall back to "weekends are off".
- **The 24 solar terms use a general day-count formula**, so a given year may be off by one day from the official almanac (the formula's exception table covers known deviations).
- **Almanac auspicious/inauspicious items are generic sets derived from the twelve day-officers**, not a day-by-day almanac; they are for reference only and do not cover date selection, deities or hourly fortune.
- **Under non-Chinese UIs the lunar content still displays in Chinese**: this is a deliberate trade-off (see Localization above). If you want it fully localized, add translations to `UiStrings` and turn the `Almanac` item lists into resources.
- **The Token board depends on opencode's schema** (the `tokens` and `model` fields inside `session_message.data`). If an opencode release changes those fields the numbers drop to 0 and the SQL must be updated; when the database is missing the footer points you to Settings.
- The **first** load of a large range on the Token board (the last 30 days reads 100MB+) still takes about a second; the same range is then milliseconds. Historical day caches live in memory only, so the first visit after a restart recomputes them.
- Token statistics accumulate **per-message usage**, which may differ slightly from opencode's own per-session totals in `session_v2.tokens_*` (per-message is finer and buckets better by time).
- Completion history for recurring to-dos is recorded but not displayed yet (it will feed statistics such as "how many times this month").
- The UI layer has no automated tests; it is guarded by 12 regression modes such as `--selftest` / `--recurrence-demo` / `--shot`.

---

## License

[MIT](LICENSE) © 2026 xiefengdaxia

All code, UI, copy and colors are original to this project; no decompiled artifacts, resource files or private APIs from any third-party program were copied.
System fonts used for rendering (such as "Microsoft YaHei UI" / "Yu Gothic UI" / "Malgun Gothic") remain under their own licenses and are not distributed with this project.
