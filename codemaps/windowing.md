# Windowing & Orchestration

> Freshness: 2026-08-05 · commit `c77517c` · covers `SceneManager.cs`, `Native/`, `Strategies/`, `Services/`, `Helpers/`
> Hand-edited 2026-09-25: One at a Time, handoff vs app activation, HIDE hook, style ownership, UI-thread follow-ups. Line counts in the tables are from the generated pass.
>
> Template equivalent: backend. Headless — no UI type is referenced from any file below.

## Win32 layer — `StageManager.Native`

Adapted from [workspacer](https://github.com/workspacer/workspacer) by Rick Button.

| File | Lines | Role |
|---|---|---|
| `WindowsManager.cs` | 559 | window registry + WinEvent hooks; the source of all window events |
| `WindowsWindow.cs` | 546 | `IWindow` over one HWND — title, class, process, state, move/resize |
| `PInvoke/Win32.Window.cs` | 235 | `SetWindowPos`, `ShowWindow`, `GetWindowPlacement`, DWM attributes |
| `PInvoke/Win32.Long.cs` | 132 | `WS`, `WS_EX` style flags as `long` |
| `PInvoke/Win32.WinEvent.cs` | 91 | `SetWinEventHook` + `EVENT_CONSTANTS` |
| `PInvoke/Win32.cs` | 64 | shared structs — `Rect`, `POINT`, `MONITORINFOEX` |
| `PInvoke/Win32Helper.cs` | 55 | style/visibility predicates |
| `WindowsDeferPosHandle.cs` | 80 | `BeginDeferWindowPos` batching, disposable |
| `VisualHelper.cs` | 86 | DPI + visual-tree geometry |
| `Interop/NativeMethods.cs` | 35 | thin extras |
| `FocusStealer.cs` | 14 | `AttachThreadInput` dance to force foreground |

### `WindowsManager`

```
WindowsManager(IEnumerable<string>? managedDisplays = null)   // null = every display
event WindowCreateDelegate? WindowCreated      (IWindow, bool firstCreate)
event WindowDelegate?       WindowDestroyed    (IWindow)      // destroyed, cloaked, or hidden by its app
event WindowUpdateDelegate? WindowUpdated      (IWindow, WindowUpdateType)
event EventHandler<IntPtr>? DesktopShortClick
IEnumerable<IWindow> Windows
Task Start()   void Stop()
IWindowsDeferPosHandle DeferWindowsPos(int count)
void SuppressNextDesktopClick()
ForegroundChange LastForegroundChange          // (Window, Previous, Time) of the foreground event being dispatched
bool WasHandoff(ForegroundChange)              // the previous active app window left within 600 ms either side
```

`WindowsWindow` reads the executable path with `QueryFullProcessImageName` under
`PROCESS_QUERY_LIMITED_INFORMATION`, never `Process.MainModule`: hardened apps (1Password,
elevated processes) refuse module access, and a failed lookup gives `ProcessId -1`, which
`RegisterWindow` drops before any filter runs.

Task Manager (`TaskManagerWindow`) is excluded only when its process runs at a higher integrity
level than ours (`RunsAboveOwnIntegrity`, token `TokenIntegrityLevel`): it auto-elevates for
administrators, and UIPI then refuses `SetWindowPos`, so it could never be parked. For a standard
account it is an ordinary scene.

`WindowUpdateType` distinguishes foreground, move-start, move-end, minimize, restore — `MainWindow` and
`DragDropManager` both filter on it rather than re-querying state.

Hooks: `EVENT_OBJECT_DESTROY..EVENT_OBJECT_HIDE` (HIDE included — an app closing to the notification
area only hides its window; it is unregistered like a cloaked one when `IsWindowVisible` confirms),
cloak/uncloak, minimize, move/size, foreground, switch-end, location and name change.

`managedDisplays` scopes the startup un-minimize (`UncloakStartupMinimized`) to displays a stage runs
on — on any other display nothing would ever make the alpha-0 window visible again.

**Style ownership.** `Win32Helper.SetAlpha` / `SetClickThrough` record the windows whose
`WS_EX_LAYERED` / `WS_EX_TRANSPARENT` they added; `ClearLayered` / `ClearClickThrough` remove only
those (`force: true` for the crash rescue, whose run is gone). A window layered by its own app (WPF
`AllowsTransparency`, `UpdateLayeredWindow`) is never given an alpha and never stripped — either
breaks its rendering for good.

Batch any multi-window reposition through `DeferWindowsPos`; individual `SetWindowPos` calls tear.

### `Native.Window` contracts

`IWindow` (59 lines) — `Handle`, `Title`, `Class`, `Location`, `Offset`, `ProcessId`,
`ProcessFileName`, `ProcessName`, `CanLayout`, `IsFocused`, `IsMinimized`, `IsMaximized`,
`IsMouseMoving`. Plus `IWindowLocation`, `IWindowsManager`, `IWindowsDeferPosHandle`, and the
`WindowState` / `WindowUpdateType` enums.

## Scene engine — `SceneManager.cs` (885 lines)

Groups windows into `Scene`s by process key and decides which scene is on stage.

```
event EventHandler<SceneChangedEventArgs>?                  SceneChanged
event EventHandler<CurrentSceneSelectionChangedEventArgs>?  CurrentSceneSelectionChanged

SceneManager(WindowsManager, bool hideDesktopIcons = true)
Task Start()                              void Stop()          void Dispose()

Scene? FindSceneForWindow(IWindow | IntPtr)
IEnumerable<Scene> GetScenes()            IEnumerable<IWindow> GetCurrentWindows()
bool IsCurrentScene(Scene?)

Task<bool> SwitchTo(Scene?)
Task MoveWindow(Scene source, IWindow, Scene target)
Task MoveWindow(IntPtr handle, Scene target)
Task PopWindowFrom(Scene source)
Scene? SeparateWindowToNewScene(IWindow)

void ParkWindow(IWindow)                  void RestoreWindow(IWindow)
void HideCurrentSceneWindows()            void RestoreMinimizedInvisibly(Scene)
void ShowDesktopIcons()                   void HideDesktopIcons()
SceneSnapshot.Snapshot CreateSnapshot()
```

`SwitchTo` is the hot path: park the outgoing scene's windows, restore the incoming ones, raise
`CurrentSceneSelectionChanged`. `MainWindow` animates around it, not inside it.

Grouping key (`GetWindowGroupKey`) is the process id — every window of one process is one scene —
or, with **One at a Time** (`SceneManager(..., oneWindowAtATime: true)`, macOS "Show windows from an
application"), `"{pid}:{hwnd}"`: every window is its own scene and the app's other windows wait in
the strip. The mode is fixed per instance; `StageHost` rebuilds the stage when the setting flips.

Delayed follow-ups run on the UI thread through `PostToUi` (context captured in `Start`), never on
the thread pool, and stand down once `Stop` has run:

| Follow-up | When | Does |
|---|---|---|
| `RefocusStage` | 300 ms after a window of the stage scene left | focus the stage's top window, unless focus is already on something visible |
| `FillEmptyStage` | 200 ms after the stage scene lost its last window — closed, moved to another display, or its last visible window minimized | most recently on-stage scene with a non-minimized window (`_lastOnStage`, stamped in `SwitchTo`) takes the stage; desktop view if none. Stands down if a window came back meanwhile. Retries while a transition runs |
| `SettleUnfocusedNewWindow` | 300 ms after a window appeared (or was shown again) unfocused with no scene | focused by then, or stacked in front of the stage (`IsInFrontOfStage`) → its scene takes the stage; else it gets a scene in the strip and is parked. Tray apps (1Password, WhatsApp) show their window without activating it |
| `SettleParkedActivation` | 80 ms after a parked window gained the foreground | see the parked-window guard below |
| `ScheduleForegroundRecheck` | 150 ms after a rapid focus change was filtered | `ReconcileForeground`, or the parked activation the burst swallowed |
| `SwitchToSceneByWindow` retry | 350 ms, ×3, after the sidebar refused a switch mid-transition | re-issue if the window still has focus |

When a scene's last window is destroyed, `WindowDestroyed` first runs `AdoptOrphanWindows` on it:
apps that replace their window (PS Remote Play, splash → main) register the successor just
before the old one goes, and it carries on in the same scene.

`IsOnScreen(Scene)` — a window of the scene is visible, not parked, not minimized. `MainWindow.AnimatedSwitchTo`
skips the card flight for such a scene (brought over from another display, raised from the tray):
flying cards over a window already on stage redrew the outgoing scene on top of it.

`PopWindowFrom` onto an empty stage (desktop view, or the stage scene just removed) brings the
dragged scene on stage instead of pulling into nothing.

### Event filtering — four guards, all load-bearing

| Guard | Where | Purpose |
|---|---|---|
| `_suspend` | `:25`, set around `SwitchTo` and every mutation path | focus events fired *during* a switch would cascade into another switch; handlers early-return |
| `IsRapidFocusChange()` | `:449` | swallows foreground events <100 ms apart, blocking system-initiated focus loops (modal dialogs, Teams compact view) |
| `IsPersistentWindow()` | filtered by `GetSceneableWindows` | keeps floating windows out of scenes: any `WS_EX_TOPMOST` window (Picture-in-Picture, mini players) and the Teams meeting compact view |
| parked-window guard | foreground handler | a parked window gaining focus after a user activation (`IsRecentUserActivation` — taskbar, Alt+Tab, switcher within 1.5 s) comes on stage at once. Otherwise `SettleParkedActivation` decides 80 ms later: `WasHandoff` (the previously active **tracked** window was minimized/hidden/closed/cloaked within 600 ms either side — Windows walking the z-order) is ignored and focus moves to the stage's top window or the desktop; anything else (link opened in the browser, notification, launcher, app raising its window) brings its scene on stage |
| desktop blank-click | `:202-234` + `Helpers/DesktopShellClassifier` | a click on `WorkerW`/`Progman` counts as blank desktop only when the `SysListView32` child reports zero selected items via `LVM_GETSELECTEDCOUNT` — separates wallpaper click from icon click, drives scene ↔ desktop toggle |

`GetSceneableWindows` is the single filter: not persistent, `CanLayout`, non-empty `ProcessFileName`,
non-empty `Title`. Anything bypassing it will pick up shell and tool windows.

## Hide/show policy — `StageManager.Strategies`

`IWindowStrategy` — `Show(IWindow)` / `Hide(IWindow)`. Three implementations, chosen by `SceneManager`:

| Strategy | Lines | Mechanism | Trade-off |
|---|---|---|---|
| `OpacityWindowStrategy` | 190 | moves the window past the virtual-screen edge | DWM keeps compositing, so previews stay live |
| `ShowAndHideWindowStrategy` | 21 | `ShowWindow(SW_HIDE / SW_SHOW)` | cheap; kills capture and some apps mishandle it |
| `NormalizeAndMinimizeWindowStrategy` | 18 | minimize / restore | most compatible; visible taskbar animation |

`OpacityWindowStrategy` is the default and is a misnomer — it does **not** set alpha. Windows.Graphics.Capture
reads DWM-composited (post-alpha) output, so an alpha=0 window yields transparent frames. Off-screen parking
is the only hide that keeps frames flowing; hidden and minimized windows produce none.

Its extra statics matter to callers:

```
static bool TryGetOriginalPosition(IntPtr hWnd, out int x, out int y)
static void CleanupWindow(IntPtr hWnd)
```

`_originalPositions` holds the pre-hide on-screen point; `SceneTransitionAnimator` reads it to target a
window's *intended* rect rather than its parked one. A per-HWND `SemaphoreSlim` serialises Show/Hide so
concurrent calls can't race on position state; `CleanupWindow` acquires before disposing and must be
called on window destroy.

`Show` restores onto a display that still exists (`OnAnExistingDisplay` clamps a saved spot no work
area intersects), drops only the styles `Win32Helper` owns, and leaves minimized windows untouched
(it used to re-minimize them with `SW_SHOWMINIMIZED`, which activates). `Hide` makes the parked
window opaque by removing our layered style rather than setting alpha 255 — Chromium stops painting
while forced layered, freezing its tile. `ReturnHome` moves a parked window its app hid back to its
saved spot, unshown, so it reappears there.

## Services — `StageManager.Services`

| File | Lines | Role |
|---|---|---|
| `UpdateService.cs` | 259 | GitHub releases check, download, apply-and-restart; `record UpdateInfo(TagName, Version, DownloadUrl, Size)` |
| `Desktop.cs` | 112 | desktop icon visibility — `GetDesktopIconsVisible`, `ShowIcons`, `HideIcons(animate)`, `RestoreIcons`, `EnsureIconsExist` |
| `ThemeManager.cs` | 60 | swaps `Themes/*.xaml`, listens for OS light/dark change |
| `SceneSnapshot.cs` | 68 | persist/restore scene layout; `record Snapshot(long[] ActiveSceneHandles, SceneEntry[] Scenes)`, `SceneEntry(Key, Handles, ProcessIds?)`; `CarryOver` hands a layout to the next stage in memory; `SessionPath` (`%LOCALAPPDATA%\StageManager\session.json`) keeps it across runs — written by `StageHost.SaveSession` on Quit and session end, read once at start via `App.SessionScenesPath`. Restore skips a handle whose process id no longer matches (handles are reused after a reboot) |
| `AutoStart.cs` | 34 | `HKCU\...\Run` registry entry |
| `Settings.cs` | 138 | HKCU registry; `Changed` event; hide icons, monitor selection (`AllMonitors` = `"*"` or a device name), ask-on-startup, stage enabled, show recent apps, recent-apps count (1–10), show windows one at a time, click-wallpaper-reveals-desktop, toggle hotkey |
| `HotkeyGesture.cs` | 75 | `record (ModifierKeys, Key)`, text round-trip `"Ctrl+Alt+S"`, needs ≥1 modifier |
| `GlobalHotkey.cs` | 80 | window-less `RegisterHotKey`, `Pressed` event, `IsRegistered` false when taken |

`UpdateService` drives a `private enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Error }`
consumed by the tray menu in `MainWindow`.

## Helpers

| File | Role |
|---|---|
| `Helpers/DesktopShellClassifier.cs` | identifies `Progman` / `WorkerW` / shell windows that must never become scenes |
| `Helpers/OverlayCoordExtensions.cs` | `ToCanvas` — screen point → overlay canvas point |

## Shutdown contract

`WM_CLOSE` is input-synchronous, so any outgoing cross-apartment COM call made while dispatching it
fails with `RPC_E_CANTCALLOUT_ININPUTSYNCCALL`. `MainWindow.OnClosing` therefore runs teardown **before**
WPF flips `IsVisible` across the tree:

```
_sceneTransitionAnimator?.Dispose()
_sidebarDragGhost?.Hide()
CompositionThumbnail.ShutdownAll()
```

Each is individually wrapped — a throw in one must not skip the others. `App.xaml.cs` deliberately does
not set `args.Handled` on unhandled exceptions, so anything escaping here kills the process mid-teardown
and leaks capture sessions that DWM keeps feeding.

## Known issues

- `SceneManager.cs` at 885 lines mixes grouping, switching, and strategy dispatch.
- `Scenes` mutations raise `CollectionChanged` synchronously and handlers re-enter; index-based
  `RemoveAt`/`Insert` needs a clamp. See `MainWindow.ApplyCurrentSceneSelection`.
- Display hot-plug rebuilds the whole stage (`StageHost.CheckDisplays`, see architecture.md):
  windows come back on screen for a moment in between. A lighter re-layout of live sidebars would
  need `MainWindow`'s geometry (`_monitorPx`, `_workPx`, `_workDip`) to stop being readonly.

## Per-display stages

One `SceneManager` per managed display, all over one shared `WindowsManager` (owned by
`StageHost`). `WindowsManager.Start/Stop` are reference-counted so only the first start hooks and
only the last stop unhooks.

```
SceneManager(WindowsManager, bool hideDesktopIcons = true, string? monitorDeviceName = null, bool oneWindowAtATime = false)
string? MonitorDeviceName          // null = every display (single-display machines)
bool    OneWindowAtATime
Scene?  FindTopmostScene()
Task<bool> SwitchTo(Scene?, bool focus = true)
```

- **Home display** of a window: `Screen.FromRectangle` of its rect, read at the
  `OpacityWindowStrategy` saved position while parked and at `rcNormalPosition` while minimized.
  Reading the live rect would assign every parked window to the display nearest the park point.
- `GetSceneableWindows` and every event handler filter on it, so instances never park, show or
  adopt each other's windows. With one display selected out of several, the other displays'
  windows are never touched.
- `ReconcileMonitor` runs on `MoveEnd` and on foreground events for off-display windows: the old
  owner drops the window from its scene (`FillEmptyStage` if the stage empties), the new display
  adopts it when focused. Each instance only edits its own scenes. It skips parked windows: the
  sidebar drag's WM_CANCELMODE raises MOVESIZEEND while the window is parked mid-flight.
  `DragDropManager` calls `WindowsManager.NotifyMoveEnd` after a taken-over drag ends, since
  Windows sends no MOVESIZEEND for that drop.
- `ReconcileForeground()` — safety net, run from the sidebar's 500 ms overlap timer (marshalled
  to the UI thread). Settles only the foreground window: on this display without a scene, or in a
  scene that is not on stage → `SwitchToSceneByWindow`; in our scene but on another display →
  `ReconcileMonitor`. Catches what events miss — Electron apps move frameless windows without a
  move loop (no MOVESIZEEND), and new windows first appear on another display. Skipped while
  `AnimatedSwitchInProgress`, for parked, minimized, moving or persistent windows.
- Desktop click: `WindowsManager.QueueDesktopClick` raises `DesktopShortClick` ~40 ms after the
  button-up (was: a full double-click interval), drops the second click of a double-click and
  presses longer than 350 ms, and records `LastDesktopClickPoint` at the button-up — the display
  test uses that, not the live cursor. Reveal runs through `SceneManager.AnimatedShowDesktop`
  (`MainWindow.AnimatedShowDesktopAsync` → `SceneTransitionAnimator.AnimateToTrayAsync`: the
  stage window flies into the tile slot reserved in the strip); restore runs through
  `AnimatedSwitch`.
- On a display with a left neighbour the sidebar strip is also the path across, so
  `DragDropManager` only starts the shrink after the pointer rests in the zone 220 ms.
- Desktop-icon toggle is shell-wide, so instances share a static `_inDesktopView` set: icons
  show while any display is in desktop view. `DesktopShortClick` only toggles the display under
  the cursor.
- Update restart: `StageHost.CreateSnapshot` merges all instances' snapshots;
  `SceneSnapshot.Load` caches its result so each instance can restore from the one file.
