# Architecture

> Freshness: 2026-08-05 · commit `c77517c` · branch `0.8.3` · 65 C# files, ~9 990 lines
> Hand-edited 2026-09-25: display-change and One-at-a-Time rebuilds, edge trigger, UI-thread overlap check.

Single WPF executable, no server, no client/server split. One project: `StageManager/StageManager.csproj`
(`net10.0-windows10.0.19041.0`, `WinExe`, WPF + WinForms interop, nullable enabled).

## Layers

```
                        App.xaml.cs                     process entry, single-instance, theme
                             |
                        MainWindow                      sidebar shell, tray menu, input routing
                       /     |      \
          SceneManager   Animations   Controls          orchestration / motion / live previews
                |            |            |
       Native (Win32)   Composition   Composition       HWND control, DWM-free capture
                |            |            |
             user32       WinRT       Direct3D 11
```

| Layer | Namespace | Owns | Map |
|---|---|---|---|
| Shell | `StageManager` | `MainWindow`, `App`, `StageHost`, `TrayController`, `SettingsWindow`, `MonitorLayout`, `MonitorPickerWindow`, `Log` | this file |
| Orchestration | `StageManager` , `.Strategies` , `.Services` | scene lifecycle, window hide/show policy, settings, updates | [windowing.md](windowing.md) |
| Windowing | `StageManager.Native.*` | HWND enumeration, WinEvent hooks, positioning | [windowing.md](windowing.md) |
| State | `StageManager.Model` | `Scene`, `SceneModel`, `WindowModel`, event args | [data.md](data.md) |
| Presentation | `StageManager.Controls` , `.Converters` , `.Animations` | tiles, overlays, transitions, drag ghosts | [rendering.md](rendering.md) |
| Capture | `StageManager.Composition.*` | frame capture, compositor visuals, D3D device | [rendering.md](rendering.md) |

Naming note: this app has no backend or frontend. `windowing.md` covers what the skill template calls
backend (headless orchestration + OS integration); `rendering.md` covers what it calls frontend.

## Namespace dependency graph

Arrows = `using` edges between internal namespaces. Acyclic.

```
MainWindow ──> Animations ──> Controls ──> Composition ──> Composition.Interop
     │              │             │              │
     │              └─> Model     └─> Native.PInvoke
     │              └─> Helpers
     ├──> SceneManager ──> Strategies ──> Native.Window
     │         └────────> Native.* , Services , Helpers , Model
     └──> Services , Native.Interop
```

Rules the graph enforces today:
- `Native.*` and `Model` never reference UI namespaces — both are reusable headless.
- `Composition` depends only on `Composition.Interop` and `Native.PInvoke`; it knows nothing of scenes.
- `Controls` is the only namespace that owns a `CaptureSession`; `Animations` borrows, never creates
  (except `LiveCardHost.TryCreateOwned`).
- `Log` reaches into `Model` for scene diagnostics — the one downward-facing exception.

## Runtime flow

1. `App.OnStartup` — `ThemeManager.ApplyTheme`, resolve displays (`MonitorPickerWindow` when more
   than one display is attached and `Settings.GetAskMonitorOnStartup()`; skipped on update restart),
   then `StageHost.Start` creates one `MainWindow` per selected display. `ShutdownMode` is
   `OnExplicitShutdown`; `StageHost.Quit` closes every sidebar, then shuts down.
2. `MainWindow.OnContentRendered` — take the shared `WindowsManager` from `StageHost`, build this
   display's `SceneManager`, subscribe to the process-wide SharpHook hook (ref-counted static),
   restore `SceneSnapshot`, warm the transition overlay. The display holding the foreground window
   switches to its scene; the others switch to their frontmost scene without taking focus.
3. `WindowsManager` WinEvent hooks raise `WindowCreated` / `WindowUpdated` / `WindowDestroyed`.
4. `SceneManager` groups windows by process key into `Scene`s, raises `SceneChanged` /
   `CurrentSceneSelectionChanged`.
5. `MainWindow` projects `Scene` → `SceneModel` into `ObservableCollection<SceneModel> Scenes`;
   XAML binds each `WindowModel.Handle` to a `CompositionThumbnail`.
6. Each tile opens a `CaptureSession` (`Windows.Graphics.Capture` → `Windows.UI.Composition`).
7. Switching or dragging hands the tile's live visual to `SceneTransitionAnimator` via `LiveCardHost`.

## Stage lifetime — `StageHost`

Lives for the whole process and owns what outlives a sidebar:

- `TrayController` — tray icon + menu (on/off toggle with its shortcut, Settings…, project page,
  update state machine, Quit). Built in code; exists while the stage is off. `TrayController.IsMenuOpen`
  gates the sidebars' mouse hook.
- `GlobalHotkey` — `RegisterHotKey(NULL, …)`; WM_HOTKEY arrives as a thread message through
  `ComponentDispatcher.ThreadFilterMessage`, so no window is needed. Default `Ctrl+Alt+S`,
  re-registered on `Settings.Changed("ToggleHotkey")`, suspended while the settings window records.
- `SettingsWindow` — single instance; writes straight through `Settings`, no Apply button.
- On/off: `SetEnabledAsync(false)` slides every sidebar off (`BeginStow`), then
  `CloseAndRestoreWindows(minimizeHidden: false)` — windows go back on screen unminimized. On builds
  a fresh `WindowsManager` and calls `CompositionThumbnail.ResetAfterShutdown()`, since the previous
  stage's `ShutdownAll` latched the capture layer shut. `Quit` closes with `minimizeHidden: true`.
- Display change from settings: `ApplyMonitorSelectionAsync` = off + on with the new selection.
- `RebuildStageAsync(reason, keepLayout)` = off + on for changes to the stage's foundations:
  - **Displays** — `SystemEvents.DisplaySettingsChanged` and `UserPreferenceChanged(Desktop)`
    (work area: taskbar moved/auto-hide) are settled for 2.5 s, then `DisplaySignature()` (device,
    bounds, work area, primary) is compared. `keepLayout: true` hands the scene groupings across in
    memory via `SceneSnapshot.CarryOver`, cleared by the next `StopStageAsync`.
  - **One at a Time** toggled — `keepLayout: false`, since every scene key changes meaning.
- `StartStage` passes the selected displays to `WindowsManager` (null when all are selected).

Live settings: `Settings` setters raise `Settings.Changed(name)`; `MainWindow.OnSettingChanged`
applies `RecentAppsCount` (strip size, was `MAX_SCENES`), `ShowRecentApps` (strip stays stowed,
edge reveals it), `HideDesktopIcons`. `ClickWallpaperRevealsDesktop` is read by `SceneManager` per click.
`ShowWindowsOneAtATime` is read when a `SceneManager` is built; `StageHost` rebuilds on change.

## Strip overflow — "More" button

When more scenes exist than `RecentAppsCount`, `MainWindow` shows a "N more" pill under the tiles
(`moreButton`, space always reserved — Hidden, never Collapsed — so tiles don't shift) and a
`Popup` listing the overflow scenes by recency. Picking one runs `AnimatedSwitchTo`; with no tile
to fly from, `sidebarSlot` is the More button's bounds and the incoming card is an icon card.
The overlap timer won't stow the strip while the list is open (`_moreListOpen`).

## Frame timing

`CompositionTarget.Rendering` is raised per render pass and repeats within one frame when
several WPF windows render. Every per-frame loop dedupes on `RenderingEventArgs.RenderingTime`
(`SceneTransitionAnimator` waits and flight, `IconOverlayWindow`, `FrameClock`); counting raw
raises made the "wait 5 frames for card pixels" elapse in ~3 ms.

## Per-display sidebars

Each `MainWindow` is bound to one `System.Windows.Forms.Screen`:

- Docked at that display's work-area left/top (`DockedLeft`/`DockedTop`), height = work-area height
  in DIPs. `GetWorkAreaBounds` reads the assigned display, never `MonitorFromWindow` — a parked
  sidebar sits beside, or on, another display.
- `ParkedLeft` is `DockedLeft - Width` unless `MonitorLayout.HasLeftNeighbour`; then the sidebar
  parks past the virtual-screen right edge and stow/unstow **snaps** instead of sliding, since the
  slide would cross the neighbour.
- Hook coordinates (physical) are compared relative to `_workPx.Left` and gated by
  `IsOnMyMonitor(x, y)`: edge trigger, overlap check, legacy pull. `DragDropManager` takes the
  sidebar's physical left edge and treats anything left of it as another display, not the buffer.
  Anything compared with the sidebar width uses `SidebarWidthPx` (DIP width × DPI) — the overlap
  check and the Flyover's "pointer still over the strip" test compared DIPs with pixels before.
- Edge trigger: `EdgeTouchPx = 2` where the left edge is a wall the pointer stops at (no left
  neighbour, no left taskbar), else `EdgeBandPx = 44`. A window dragged into the buffer
  (`DragDropManager.IsShrinkingIntoStrip`) also brings a stowed strip out, with the ghost's overlay
  re-raised above it.
- `OverlapCheck` (500 ms `System.Threading.Timer`) only queues itself onto the dispatcher, one at a
  time: the scene's window list and the tracked windows are UI-thread state.
- DIP ↔ px uses `MonitorLayout.PixelsPerDip` (system DPI — the process has no per-monitor manifest).

## External dependencies

| Package | Version | Used for |
|---|---|---|
| Vortice.Direct3D11 / Vortice.DXGI | 3.8.1 | D3D11 device behind the capture frame pool |
| SharpHook | 6.1.2 | global low-level mouse hook (edge trigger, drag tracking) |
| Hardcodet.NotifyIcon.Wpf | 2.0.1 | tray icon and menu |
| WpfScreenHelper | 2.1.1 | monitor/work-area geometry |
| ControlzEx | 7.0.1 | window chrome helpers |
| AsyncAwaitBestPractices | 9.0.0 | `SafeFireAndForget` |

WinRT surface (`Windows.Graphics.Capture`, `Windows.UI.Composition`, `Windows.Graphics.DirectX`) comes
from the `10.0.19041.0` target platform, not a package. That platform floor is what sets the app's
minimum OS.

## Diagnostics — `Log.cs`

Every method is `[Conditional("DEBUG")]`, so logging compiles away entirely in Release. Output goes
through a `TextWriterTraceListener` to `stagemanager.log` beside the executable
(`AppContext.BaseDirectory`). Tag strings (`CAPSESS`, `COMPTHUMB`, `ANIM`, `TILT`, `SHUTDOWN`) are how
sessions are read back; `MainWindow.xaml.cs:1055` emits a fixed-format geometry line that is parsed, so
its shape is an invariant.

## Build

- SDK pinned by `global.json` to `10.0.100-rc.2.25502.107`, `rollForward: latestPatch`.
- `SetVersionFromGitTag` target runs `git describe --tags --abbrev=0`, strips `v`, sets `Version`.
  A build with no tag reachable stays at `1.0.0`.
- `Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport=true` — WPF's `PenThreadWorker`
  throws on its own thread, which no handler can catch. App takes no pen input.
- Release workflow: `.github/workflows/dotnet-desktop.yml`, x64 + arm64.

## Known structural debt

- `MainWindow.xaml.cs` is 1 788 lines / ~70 members and mixes six concerns: scene projection, sidebar
  drag, visibility animation, icon overlay, and window-mode switching. The update UI and tray menu
  moved out to `TrayController`; the drag region is the largest remaining extraction candidate.
- `SceneManager.cs` at 885 lines couples grouping, switching, and strategy dispatch.
