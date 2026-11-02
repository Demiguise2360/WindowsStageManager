using AsyncAwaitBestPractices;
using StageManager.Helpers;
using StageManager.Model;
using StageManager.Native;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using StageManager.Services;
using StageManager.Strategies;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;

namespace StageManager
{
	public class SceneManager : IDisposable
	{
		private readonly Desktop _desktop;
		private List<Scene> _scenes = new List<Scene>();
		private readonly object _scenesLock = new object();
		private Scene? _current;
		private bool _suspend = false;
		private Scene? _lastScene; // remembers the scene that was active before desktop view
		private IWindow? _lastFocusedWindow;
		private DateTime _lastFocusChange = DateTime.MinValue; // Track rapid focus changes

		// Windows the user minimized themselves. A minimized window is never parked —
		// OpacityWindowStrategy.Hide skips iconic windows — so nothing else records that it
		// left the stage on purpose, and the restore passes in SwitchTo and
		// RestoreMinimizedInvisibly would drag it back on the next scene switch.
		private readonly HashSet<IntPtr> _userMinimized = new HashSet<IntPtr>();

		/// <summary>
		/// When set, focus-triggered scene switches use this delegate instead of calling SwitchTo directly.
		/// MainWindow sets this to inject the transition animation.
		/// </summary>
		public Func<Scene, Task<bool>>? AnimatedSwitch { get; set; }

		/// <summary>
		/// Desktop-view counterpart of <see cref="AnimatedSwitch"/>: when set, revealing the desktop
		/// runs through it so the stage flies into the strip instead of vanishing.
		/// </summary>
		public Func<Task<bool>>? AnimatedShowDesktop { get; set; }

		public event EventHandler<SceneChangedEventArgs>? SceneChanged;
		public event EventHandler<CurrentSceneSelectionChangedEventArgs>? CurrentSceneSelectionChanged;

		// Use full-transparency instead of minimising so hidden windows keep repainting and thumbnails stay live.
		private IWindowStrategy WindowStrategy { get; } = new OpacityWindowStrategy();

		public WindowsManager WindowsManager { get; }

		private const string TeamsProcessName1 = "ms-teams.exe";
		private const string TeamsProcessName2 = "teams.exe";
		private bool _disposed = false;
		private bool _hideDesktopIcons;

		/// <summary>
		/// How <see cref="Stop"/> leaves the windows it puts back. True (quitting) minimizes all but
		/// the focused one, so the scenes that were parked do not all land on screen at once.
		/// False (Stage Manager turned off) leaves every window where it was, as macOS does.
		/// </summary>
		public bool MinimizeOnStop { get; set; } = true;

		/// <summary>
		/// Whether scenes hide the desktop icons. Changing it applies at once to the icons;
		/// later scene switches follow it.
		/// </summary>
		public bool HideDesktopIconsEnabled
		{
			get => _hideDesktopIcons;
			set
			{
				if (_hideDesktopIcons == value) return;
				_hideDesktopIcons = value;

				bool anyDesktopView;
				lock (_inDesktopView)
					anyDesktopView = _inDesktopView.Count > 0;

				if (value && !anyDesktopView)
					_desktop.HideIcons();
				else
					_desktop.ShowIcons();
			}
		}

		// The display this instance runs the stage for, by device name (\\.\DISPLAYn), or null
		// for every display at once. Each display gets its own SceneManager over one shared
		// WindowsManager; this is what keeps them from grabbing each other's windows.
		private readonly string? _monitorDeviceName;

		// Desktop icons are one shell-wide switch, so per-display instances have to agree on it:
		// icons show while ANY display is in desktop view. A display the user just clicked to
		// the desktop expects them, and on the others its windows cover them anyway.
		private static readonly HashSet<SceneManager> _inDesktopView = new();

		public string? MonitorDeviceName => _monitorDeviceName;

		/// <summary>
		/// True when <paramref name="window"/> belongs on this instance's display.
		/// <para>
		/// Decided by where the window lives when it is on stage: a parked window is read at its
		/// saved on-screen position, a minimized one at its restored rect. Reading the live rect
		/// would put every parked window on whichever display is nearest the park point.
		/// </para>
		/// </summary>
		private bool IsOnMyMonitor(IWindow window)
		{
			if (_monitorDeviceName is null)
				return true;

			return string.Equals(HomeMonitorOf(window.Handle), _monitorDeviceName, StringComparison.OrdinalIgnoreCase);
		}

		private static string? HomeMonitorOf(IntPtr handle)
		{
			System.Drawing.Rectangle rect;
			if (Win32.IsIconic(handle))
			{
				// A minimized window reports a placeholder rect near (-32000,-32000).
				var wp = new Win32.WINDOWPLACEMENT { length = Marshal.SizeOf<Win32.WINDOWPLACEMENT>() };
				if (!Win32.GetWindowPlacement(handle, ref wp))
					return null;
				var n = wp.rcNormalPosition;
				rect = System.Drawing.Rectangle.FromLTRB(n.Left, n.Top, n.Right, n.Bottom);
			}
			else
			{
				var r = new Win32.Rect();
				if (!Win32.GetWindowRect(handle, ref r))
					return null;
				rect = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
			}

			if (OpacityWindowStrategy.TryGetOriginalPosition(handle, out var x, out var y))
				rect.Location = new System.Drawing.Point(x, y);

			return System.Windows.Forms.Screen.FromRectangle(rect).DeviceName;
		}

		/// <summary>
		/// A window event concerns this instance when the window is in one of its scenes or
		/// sits on its display. Everything else is another display's business.
		/// </summary>
		private bool IsMine(IWindow window) => FindSceneForWindow(window) is not null || IsOnMyMonitor(window);

		/// <summary>
		/// Determines whether the given window should stay visible across scenes and therefore must not
		/// participate in Stage Manager scene logic. Currently hard-codes an exception for the Microsoft
		/// Teams ‘Meeting compact’ floating pop-up.
		/// </summary>
		private bool IsPersistentWindow(IWindow window)
		{
			if (window == null)
				return false;

			// Always-on-top windows float above every stage, like floating panels on macOS:
			// Picture-in-Picture video, mini players, pinned notes. Grouping one into a scene
			// would park it whenever another app takes the stage.
			if (((long)Win32.GetWindowExStyleLongPtr(window.Handle) & (long)Win32.WS_EX.WS_EX_TOPMOST) != 0)
				return true;

			// Quick process check – bail out early if it is definitely not Teams
			var exe = window.ProcessFileName ?? string.Empty;
			if (!string.Equals(exe, TeamsProcessName1, StringComparison.OrdinalIgnoreCase) &&
				!string.Equals(exe, TeamsProcessName2, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			// Identify the floating meeting pop-up through its title. The compact meeting view always contains
			// the words “Meeting” and “compact”. Adjust the checks here if Microsoft changes the wording.
			var title = window.Title ?? string.Empty;
			return title.IndexOf("Meeting", StringComparison.OrdinalIgnoreCase) >=0 &&
			 title.IndexOf("compact", StringComparison.OrdinalIgnoreCase) >=0;
		}

		/// <summary>
		/// macOS "Show windows from an application: One at a Time". Every window is a scene of its
		/// own, so an app's other windows wait in the strip and clicking one swaps it onto the
		/// stage. Off ("All at Once") groups every window of a process into one scene. Fixed for
		/// the lifetime of the instance — the host rebuilds the stage when the setting changes,
		/// because it changes what every existing scene key means.
		/// </summary>
		public bool OneWindowAtATime { get; }

		// The UI thread's context, captured in Start. Delayed follow-ups (refocus after a close,
		// filling an emptied stage) must run there: SwitchTo and the scene list are UI-thread
		// state, and the sidebar handlers they raise assume it.
		private SynchronizationContext? _uiContext;
		private volatile bool _stopped;

		// When each scene was last on stage. Picks the scene that takes over an emptied stage —
		// the most recently used one, the way the next app comes forward on macOS when one quits.
		private readonly Dictionary<Scene, long> _lastOnStage = new();
		private long _stageClock;

		public SceneManager(WindowsManager windowsManager, bool hideDesktopIcons = true, string? monitorDeviceName = null, bool oneWindowAtATime = false)
		{
			WindowsManager = windowsManager ?? throw new ArgumentNullException(nameof(windowsManager));
			_desktop = new Desktop();
			_hideDesktopIcons = hideDesktopIcons;
			_monitorDeviceName = monitorDeviceName;
			OneWindowAtATime = oneWindowAtATime;

			// Ensure icons exist (shell-level) so the SysListView32 is available for alpha fading.
			// If previous session crashed, icons may have been toggled off — restore them.
			_desktop.EnsureIconsExist();

			if (_hideDesktopIcons)
				_desktop.HideIcons(animate: false);

			Log.Info("STARTUP", $"SceneManager constructor: hideDesktopIcons={_hideDesktopIcons} monitor={_monitorDeviceName ?? "(all)"} oneWindowAtATime={OneWindowAtATime}");
		}

		/// <summary>
		/// Runs <paramref name="action"/> on the UI thread after <paramref name="delayMs"/>, unless
		/// the stage has been stopped by then. Exceptions are logged, not rethrown: an escape here
		/// reaches the dispatcher's unhandled-exception handler, which ends the process.
		/// </summary>
		private void PostToUi(int delayMs, Action action)
		{
			var ctx = _uiContext;
			void Run()
			{
				if (_stopped) return;
				try { action(); }
				catch (Exception ex) { Log.Info("SCENE", $"Deferred action failed: {ex.GetType().Name}: {ex.Message}"); }
			}

			Task.Delay(delayMs).ContinueWith(_ =>
			{
				if (ctx is null) Run();
				else ctx.Post(_ => Run(), null);
			}, TaskScheduler.Default);
		}

		/// <summary>
		/// The scene most recently on stage, other than <paramref name="except"/>, that still has
		/// a window to show — one the user has not minimized. Scenes never on stage yet rank
		/// last, in list order.
		/// </summary>
		private Scene? MostRecentScene(Scene? except)
		{
			lock (_scenesLock)
			{
				return _scenes
					.Where(s => !ReferenceEquals(s, except) && s.Windows.Any(w => !IsUserMinimized(w)))
					.OrderByDescending(s => _lastOnStage.TryGetValue(s, out var t) ? t : 0)
					.FirstOrDefault();
			}
		}

		private void ForgetScene(Scene scene)
		{
			lock (_scenesLock)
			{
				_scenes.Remove(scene);
				_lastOnStage.Remove(scene);
			}
		}

		public async Task Start()
		{
			// Check if we're on the UI thread by verifying we have access to the dispatcher
			// This is more reliable than checking for thread ID1
			if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() == false)
				throw new NotSupportedException("Start has to be called on the main thread, otherwise events won't be fired.");

			_uiContext = SynchronizationContext.Current;

			Log.Info("STARTUP", "SceneManager starting");

			WindowsManager.WindowCreated += WindowsManager_WindowCreated;
			WindowsManager.WindowUpdated += WindowsManager_WindowUpdated;
			WindowsManager.WindowDestroyed += WindowsManager_WindowDestroyed;
			WindowsManager.DesktopShortClick += WindowsManager_DesktopShortClick;

			await WindowsManager.Start();

			Log.Info("STARTUP", "SceneManager started, WindowsManager active");
		}

		internal void Stop()
		{
			if (_stopped) return;
			_stopped = true;

			// Unsubscribe from all WindowsManager events to prevent memory leaks
			WindowsManager.WindowCreated -= WindowsManager_WindowCreated;
			WindowsManager.WindowUpdated -= WindowsManager_WindowUpdated;
			WindowsManager.WindowDestroyed -= WindowsManager_WindowDestroyed;
			WindowsManager.DesktopShortClick -= WindowsManager_DesktopShortClick;

			lock (_inDesktopView)
				_inDesktopView.Remove(this);

			// Restore opacity BEFORE WindowsManager.Stop() clears _windows. Only this display's
			// windows: the others belong to another instance, or to no instance at all when the
			// stage runs on a single display, and must be left exactly as they are.
			var exemptHandle = _lastFocusedWindow?.Handle ?? Win32.GetForegroundWindow();
			foreach (var w in WindowsManager.Windows.Where(IsOnMyMonitor).ToArray())
			{
				WindowStrategy.Show(w);
				if (MinimizeOnStop && w.Handle != exemptHandle)
				{
					w.ShowMinimized();
				}
			}

			WindowsManager.Stop();

			if (_hideDesktopIcons)
				_desktop.RestoreIcons();
		}

		private void WindowsManager_WindowUpdated(IWindow window, WindowUpdateType type)
		{
			if (_suspend)
			{
				Log.Window("EVENT", $"SUSPENDED, ignoring {type}", window);
				return;
			}

			if (type == WindowUpdateType.MoveEnd)
			{
				ReconcileMonitor(window);
				return;
			}

			if (type == WindowUpdateType.Foreground)
			{
				// Focus on another display is that display's business. If the window is still
				// in one of ours it was moved over there without a drag we saw (Win+Shift+Arrow,
				// an app repositioning itself) — let it go so the other display can take it.
				if (!IsOnMyMonitor(window))
				{
					ReconcileMonitor(window);
					return;
				}

				// A parked window is off-screen by our own hand. Two very different things can
				// activate one:
				// - Windows handing the foreground on because the active window was minimized,
				//   hidden or closed. Parked windows are still in the z-chain (Hide keeps their
				//   z-order deliberately), so the next one down often gets it. Following that
				//   would yank the stage to a scene the user never picked — and would undo the
				//   stow OnWindowMinimized just did. The handoff is recognised by the departure
				//   of the window that had the foreground, matched on WinEvent timestamps.
				// - Anything else: the taskbar or Alt+Tab (the Windows equivalent of clicking an
				//   app in the Dock), a link opening in the browser, a notification, an app
				//   raising its own window. That is the app coming forward and its scene must
				//   come on stage — ignoring it left the user with focus in a window they could
				//   not see and nothing visibly happening.
				// The user asking is known at once. Anything else is settled a moment later, once
				// a departure that may be queued behind this event has arrived too.
				var parked = OpacityWindowStrategy.TryGetOriginalPosition(window.Handle, out _, out _);
				if (parked && !WindowsManager.IsRecentUserActivation())
				{
					var change = WindowsManager.LastForegroundChange;
					PostToUi(HandoffSettleMs, () => SettleParkedActivation(window, change));
					return;
				}

				// Skip rapid focus changes to prevent scene switching loops. The last change of a
				// burst is the one that counts, and it is exactly the one this drops — so look
				// again once the burst has settled instead of leaving it to the 500 ms net.
				if (IsRapidFocusChange())
				{
					Log.Window("FOCUS", "RAPID focus change, skipping", window);
					ScheduleForegroundRecheck(parked ? window : null);
					return;
				}

				if (parked)
					Log.Window("FOCUS", "Parked window activated from taskbar / task switcher", window);

				Log.Window("FOCUS", "Foreground change", window);

				_lastFocusedWindow = window; // remember for scene restore
				SwitchToSceneByWindow(window).SafeFireAndForget();
			}
			else if (type == WindowUpdateType.MinimizeStart)
			{
				if (IsMine(window))
					OnWindowMinimized(window);
			}
			else if (type == WindowUpdateType.MinimizeEnd)
			{
				OnWindowRestored(window).SafeFireAndForget();
			}
			// Some applications surface a previously hidden window with a simple ShowWindow
			// call that does NOT bring the window to the foreground. In that case the
			// window is visible but still carries WS_EX_TRANSPARENT from our hide logic
			// and is therefore not clickable. Treat a Show event as a signal that the
			// application wants to interact again and restore normal interactivity.
			else if (type == WindowUpdateType.Show && IsMine(window))
			{
				Log.Window("EVENT", "Show", window);

				// Option2: Make Show event authoritative for current scene windows
				var scene = FindSceneForWindow(window);
				if (scene is not null && ReferenceEquals(scene, _current))
				{
					// If the window is minimized but just got shown, ensure it is restored
					if (window.IsMinimized)
					{
						Log.Window("EVENT", "Show: restoring minimized window in current scene", window);
						window.ShowNormal();
					}

					// Force clearing opacity/mouse-through regardless of skip checks
					WindowStrategy.Show(window);
				}
				else
				{
					// Restore normal interactivity for non-active scenes;
					// WindowStrategy.Show handles its own skip-checks internally.
					WindowStrategy.Show(window);
				}

				// Only switch scenes if this is actually a focus change, not just a show event
				// This prevents scene creation for minimized windows that shouldn't create scenes
				if (window.IsFocused)
				{
					Log.Window("EVENT", "Show + focused → switching scene", window);
					// Bring Stage Manager's focus model in sync by switching to the scene
					// containing this window. This guarantees proper stacking order and
					// icon visibility handling.
					SwitchToSceneByWindow(window).SafeFireAndForget();
				}
				else if (scene is null)
				{
					// Shown again without the focus and in no scene: settle it like a new window.
					PostToUi(NewWindowSettleMs, () => SettleUnfocusedNewWindow(window));
				}
			}
		}

		/// <summary>
		/// True when the user minimized this window and has not restored it since. Such a
		/// window stays off the stage even when its scene comes back — the same way macOS
		/// leaves a minimized window in the Dock while its group is on stage.
		/// </summary>
		private bool IsUserMinimized(IWindow window)
		{
			lock (_userMinimized)
				return _userMinimized.Contains(window.Handle);
		}

		/// <summary>
		/// Drops the user-minimized mark, for callers that un-minimize a window on the user's
		/// behalf (dragging its tile out of the sidebar, moving it between scenes). Clearing it
		/// before the ShowWindow call also stops the resulting MINIMIZEEND from being read as
		/// the user restoring the window from the taskbar.
		/// </summary>
		public void ForgetUserMinimized(IWindow window)
		{
			lock (_userMinimized)
				_userMinimized.Remove(window.Handle);
		}

		/// <summary>
		/// Handles the user minimizing a window. The window itself leaves the stage for good
		/// until it is restored; when it was the last one keeping the current scene on screen,
		/// the whole scene is stowed back into the sidebar and the desktop is shown — the same
		/// end state a blank desktop click produces.
		/// </summary>
		private void OnWindowMinimized(IWindow window)
		{
			if (_suspend || IsPersistentWindow(window))
				return;

			lock (_userMinimized)
				_userMinimized.Add(window.Handle);

			Log.Window("MINIMIZE", "User minimized", window);

			var scene = FindSceneForWindow(window);
			if (scene is null || !ReferenceEquals(scene, _current))
				return;

			// Exclude this handle explicitly rather than trusting IsMinimized: MINIMIZESTART
			// fires at the start of the animation and the iconic flag may not be set yet.
			if (scene.Windows.Any(w => w.Handle != window.Handle && !w.IsMinimized))
				return;

			// Once the minimize animation is under way, the most recently used scene takes the
			// stage — as when the app quits — rather than leaving the user on an empty desktop.
			Log.Action($"Last window of '{scene.Title}' minimized → most recent scene takes the stage");
			PostToUi(200, () => FillEmptyStage(scene));
		}

		/// <summary>
		/// Brings scene membership back in line with where <paramref name="window"/> now is.
		/// A window of ours that ended up on another display leaves its scene; a window that
		/// arrived on this display and is focused joins (or forms) its app's scene here and
		/// takes the stage — the same as macOS moving a window to another display's stage.
		/// <para>
		/// Both sides run this for the same event, each against its own scenes, so no instance
		/// needs to know about the others.
		/// </para>
		/// </summary>
		private void ReconcileMonitor(IWindow window)
		{
			if (_monitorDeviceName is null || IsPersistentWindow(window))
				return;

			// Parked mid-drag: the sidebar drag cancels the OS move loop (raising MOVESIZEEND)
			// and parks the window while the pointer is still travelling. Where it "is" now is
			// only where the pointer happened to be — the real drop reports again when it lands.
			if (OpacityWindowStrategy.TryGetOriginalPosition(window.Handle, out _, out _))
				return;

			var owner = FindSceneForWindow(window);
			var onMine = IsOnMyMonitor(window);

			if (owner is not null && !onMine)
			{
				ReleaseWindow(owner, window);
			}
			else if (owner is null && onMine && window.IsFocused && IsSceneable(window))
			{
				Log.Window("MONITOR", "Window arrived from another display", window);
				_lastFocusedWindow = window;
				SwitchToSceneByWindow(window).SafeFireAndForget();
			}
		}

		/// <summary>
		/// Safety net run twice a second by the sidebar: settles the FOREGROUND window.
		/// <para>
		/// Events alone miss too much. Electron apps (Razer Synapse, Discord, …) move their
		/// frameless windows themselves, so no MOVESIZEEND ever arrives when one is dragged onto
		/// this display; a new window can open on another display and be moved here before it
		/// ever changes focus again. Either way the window sat on this stage, focused, with the
		/// previous app still on stage beside it.
		/// </para>
		/// Only the foreground window is checked — it is the one the user is looking at, and it
		/// keeps this to a couple of user32 calls.
		/// </summary>
		public void ReconcileForeground()
		{
			if (_suspend || AnimatedSwitchInProgress?.Invoke() == true)
				return;

			var fg = Win32.GetForegroundWindow();
			var window = WindowsManager.Windows.FirstOrDefault(w => w.Handle == fg);
			if (window is null || window.IsMouseMoving || window.IsMinimized || IsPersistentWindow(window))
				return;
			// Parked: focus handed over by Windows, not a window the user can see.
			if (OpacityWindowStrategy.TryGetOriginalPosition(window.Handle, out _, out _))
				return;
			var owner = FindSceneForWindow(window);
			var onMine = IsOnMyMonitor(window);

			if (!IsSceneable(window))
			{
				// Diagnostic, once per window: a focused window on this display that can never
				// join a scene (no title, tool window, …) is the usual reason a window dragged
				// onto the stage leaves the previous app standing.
				if (onMine && _reportedUnsceneable.Add(window.Handle))
					Log.Window("RECONCILE", $"Focused window on this display is not sceneable (title='{window.Title}' canLayout={window.CanLayout})", window);
				return;
			}

			if (owner is not null && !onMine)
			{
				ReconcileMonitor(window);
			}
			else if (onMine && owner is null && _current is not null
				&& string.Equals(GetWindowGroupKey(window), _current.Key, StringComparison.OrdinalIgnoreCase))
			{
				// Same app as the stage: it joins that group and both stay visible — windows are
				// grouped per app, like macOS with "show all windows of an app at once".
				Log.Window("RECONCILE", $"Focused window joins the app on stage '{_current.Title}'", window);
				SwitchToSceneByWindow(window).SafeFireAndForget();
			}
			else if (onMine && (owner is null || !ReferenceEquals(owner, _current)))
			{
				// On this display and in focus, but its scene is not the one on stage: bring it
				// on, which sends the previous app to the strip.
				Log.Window("RECONCILE", owner is null ? "Focused window on this display has no scene" : "Focused window's scene is not on stage", window);
				_lastFocusedWindow = window;
				SwitchToSceneByWindow(window).SafeFireAndForget();
			}
		}

		private readonly HashSet<IntPtr> _reportedUnsceneable = new();

		/// <summary>Set by the sidebar: true while a transition or drag is running.</summary>
		public Func<bool>? AnimatedSwitchInProgress { get; set; }

		/// <summary>
		/// Drops a window that moved to another display from <paramref name="scene"/>. Unlike a
		/// destroyed window it keeps its strategy state — it is alive and on screen elsewhere.
		/// </summary>
		private void ReleaseWindow(Scene scene, IWindow window)
		{
			Log.Window("MONITOR", $"Window left for another display, removing from '{scene.Title}'", window);

			scene.Remove(window);
			ForgetZOrder(window.Handle);
			ForgetUserMinimized(window);
			if (ReferenceEquals(_lastFocusedWindow, window))
				_lastFocusedWindow = null;

			if (scene.Windows.Any())
			{
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));
				return;
			}

			ForgetScene(scene);
			SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Removed));

			// The stage just lost its last window to another display: the most recently used
			// scene here takes over, the same as when the window is closed or minimized.
			if (ReferenceEquals(scene, _current))
				PostToUi(200, () => FillEmptyStage(scene));
		}

		/// <summary>
		/// Handles the user restoring a minimized window from the taskbar. Switches to the
		/// window's scene explicitly instead of leaving it to the foreground event that
		/// follows: the window may still be parked off-screen from an earlier scene switch,
		/// and the parked-window guard in the foreground handler would (correctly) drop it.
		/// </summary>
		private async Task OnWindowRestored(IWindow window)
		{
			bool wasUserMinimized;
			lock (_userMinimized)
				wasUserMinimized = _userMinimized.Remove(window.Handle);

			// Our own restore passes call ShowNormal and echo a MINIMIZEEND straight back
			// here. Those windows were never marked, so the flag doubles as the echo guard.
			if (!wasUserMinimized || _suspend)
				return;

			Log.Window("MINIMIZE", "User restored from taskbar", window);

			var scene = FindSceneForWindow(window);
			if (scene is null)
				return;

			if (ReferenceEquals(scene, _current))
			{
				WindowStrategy.Show(window);
				return;
			}

			if (AnimatedSwitch != null)
				await AnimatedSwitch(scene);
			else
				await SwitchTo(scene);
		}

		private bool IsBlankDesktopClick(IntPtr handle)
		{
			var cls = DesktopShellClassifier.GetClassName(handle);

			// Ignore taskbar / other common shells
			if (string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(cls, "TrayNotifyWnd", StringComparison.OrdinalIgnoreCase))
				return false;

			// Helper local function to evaluate selection count on a SysListView32 window
			static bool IsListViewSelectionEmpty(IntPtr listView)
			{
				if (listView == IntPtr.Zero)
					return true;

				var sel = Win32.SendMessage(listView, Win32.LVM_GETSELECTEDCOUNT, IntPtr.Zero, IntPtr.Zero);
				return sel == IntPtr.Zero;
			}

			// Desktop background container windows (WorkerW/Progman): blank click only when no icon selected
			if (DesktopShellClassifier.IsDesktopBackground(handle))
			{
				var shell = Desktop.FindWindowEx(handle, IntPtr.Zero, "SHELLDLL_DefView", null);
				var listView = shell != IntPtr.Zero ? Desktop.FindWindowEx(shell, IntPtr.Zero, "SysListView32", null) : IntPtr.Zero;
				return IsListViewSelectionEmpty(listView);
			}

			// Desktop icon view (list view) – ensure no icon is selected
			if (DesktopShellClassifier.IsDesktopIconView(handle))
				return IsListViewSelectionEmpty(handle);

			return false;
		}

		// MainWindow assigns this so SceneManager can ignore desktop-toggle clicks while the
		// app-filter is active — those clicks are reserved for clearing the filter (handled in
		// MainWindow.OnMousePressed).
		public Func<bool>? IsAppFilterActive { get; set; }

		private void WindowsManager_DesktopShortClick(object? sender, IntPtr handle)
		{
			if (_suspend)
				return;

			// macOS "Click wallpaper to reveal desktop" turned off.
			if (!Settings.GetClickWallpaperRevealsDesktop())
				return;

			// The desktop window spans every display, so the handle says nothing about where
			// the click landed. The click point does: only that display toggles. It is the point
			// recorded at the button-up — the live cursor may already be on another display.
			if (_monitorDeviceName is not null)
			{
				var clicked = System.Windows.Forms.Screen.FromPoint(WindowsManager.LastDesktopClickPoint);
				if (!string.Equals(clicked.DeviceName, _monitorDeviceName, StringComparison.OrdinalIgnoreCase))
					return;
			}

			if (IsAppFilterActive?.Invoke() == true)
			{
				Log.Info("DESKTOP", "Click during app-filter — desktop toggle suppressed");
				return;
			}

			// Only treat clicks on truly blank desktop areas as a toggle trigger
			if (!IsBlankDesktopClick(handle))
			{
				Log.Info("DESKTOP", "Click on desktop icon, not blank area — ignoring", handle);
				return;
			}

			// Both directions animate when the sidebar offers it: the stage flies into the strip,
			// and back out of it. Without, windows just vanished and reappeared.
			if (_current is null)
			{
				Log.Action($"Desktop click → restore last scene '{_lastScene?.Title}'");
				if (_lastScene is object)
					(AnimatedSwitch is { } animated ? animated(_lastScene) : SwitchTo(_lastScene)).SafeFireAndForget();
			}
			else
			{
				Log.Action($"Desktop click → show desktop (hiding '{_current?.Title}')");
				(AnimatedShowDesktop is { } animated ? animated() : SwitchTo(null)).SafeFireAndForget();
			}
		}

		private void WindowsManager_WindowDestroyed(IWindow window)
		{
			Log.Window("EVENT", "WindowDestroyed", window);

			// Gone from tracking is not always gone from the system: an app closing to the
			// notification area only hides its window. Send a parked one home and take our
			// styles off while it is hidden, so it comes back where it lived — not at the park
			// point, from where it would be clamped into a corner — when the app shows it again.
			if (Win32.IsWindow(window.Handle))
			{
				OpacityWindowStrategy.ReturnHome(window.Handle);
				Win32Helper.ClearClickThrough(window.Handle);
				Win32Helper.ClearLayered(window.Handle);
			}

			OpacityWindowStrategy.CleanupWindow(window.Handle);
			ForgetZOrder(window.Handle);
			ForgetUserMinimized(window);

			var scene = FindSceneForWindow(window);
			if (scene is null)
				return;

			scene.Remove(window);
			Log.Scene("Window removed from scene", scene, window);

			// Some apps replace their window rather than close it (PS Remote Play, splash →
			// main window): the successor is registered, unfocused, just before the old one
			// goes. It carries on in the same scene instead of being left without one.
			if (!scene.Windows.Any())
			{
				AdoptOrphanWindows(scene);
				foreach (var successor in scene.Windows)
				{
					if (ReferenceEquals(scene, _current))
						WindowStrategy.Show(successor);
					else
						WindowStrategy.Hide(successor);
				}
			}

			if (scene.Windows.Any())
			{
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));

				// The window that went may have held the focus; make sure it lands on what is
				// left of the stage rather than on whatever Windows found next in the z-order.
				if (ReferenceEquals(scene, _current))
					PostToUi(300, () => RefocusStage(scene));
				return;
			}

			Log.Scene("Scene empty, removing", scene);
			ForgetScene(scene);
			SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Removed));

			// The stage lost its last window — on Windows that is usually the app quitting.
			// Bring the most recently used app forward, as macOS does when the active app quits.
			if (ReferenceEquals(scene, _current))
				PostToUi(200, () => FillEmptyStage(scene));
		}

		/// <summary>
		/// Focuses the top window left on stage after one of its windows went away, unless focus
		/// already sits somewhere the user can see.
		/// </summary>
		private void RefocusStage(Scene scene)
		{
			// The user (or an app) has moved on in the meantime.
			if (!ReferenceEquals(scene, _current) || _suspend || AnimatedSwitchInProgress?.Invoke() == true)
				return;

			var fg = Win32.GetForegroundWindow();
			if (scene.Windows.Any(w => w.Handle == fg))
				return;

			// Focus on some other window that is on screen was given on purpose; leave it.
			var focusLost = fg == IntPtr.Zero
				|| OpacityWindowStrategy.TryGetOriginalPosition(fg, out _, out _)
				|| DesktopShellClassifier.IsDesktopShell(fg);
			if (!focusLost)
				return;

			if (TopmostWindowOf(scene) is { } top)
			{
				Log.Window("FOCUS", "Refocusing the stage after a window left it", top);
				WindowStrategy.Show(top);
				top.Focus();
			}
		}

		/// <summary>
		/// Called a moment after the scene on stage lost its last window: the most recently used
		/// scene takes the stage, or desktop view when none is left.
		/// </summary>
		private void FillEmptyStage(Scene emptied)
		{
			// Something already took the stage (a window of the app reappeared, the user picked
			// a tile).
			if (!ReferenceEquals(_current, emptied) || _suspend)
				return;

			// A minimized stage whose window was restored in the meantime is not empty any more.
			if (emptied.Windows.Any(w => !w.IsMinimized))
				return;

			// A transition or drag is running. It may settle the stage itself; if not, the
			// stage must not stay pointed at a scene that no longer exists.
			if (AnimatedSwitchInProgress?.Invoke() == true)
			{
				PostToUi(300, () => FillEmptyStage(emptied));
				return;
			}

			var next = MostRecentScene(except: emptied);
			if (next is null)
			{
				Log.Action("Stage emptied, no scene left → desktop view");
				SwitchTo(null).SafeFireAndForget();
				return;
			}

			Log.Action($"Stage emptied → most recent scene '{next.Title}'");
			(AnimatedSwitch is { } animated ? animated(next) : SwitchTo(next)).SafeFireAndForget();
		}

		public Scene? FindSceneForWindow(IWindow window) => FindSceneForWindow(window.Handle);

		public Scene? FindSceneForWindow(IntPtr handle)
		{
			lock (_scenesLock)
				return _scenes.FirstOrDefault(s => s.Windows.Any(w => w.Handle == handle));
		}

		private Scene? FindSceneForProcess(string processName)
		{
			lock (_scenesLock)
				return _scenes.FirstOrDefault(s => string.Equals(s.Key, processName, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		/// Pulls tracked windows of the same app that belong to no scene into
		/// <paramref name="scene"/>. A window only joins a scene when it gains focus, so an
		/// app that opens several windows at once leaves every unfocused one orphaned —
		/// parked off-screen with no tile and no way of ever coming back.
		/// </summary>
		private void AdoptOrphanWindows(Scene scene)
		{
			foreach (var window in WindowsManager.Windows.ToArray())
			{
				if (IsPersistentWindow(window) || FindSceneForWindow(window) is not null || !IsOnMyMonitor(window))
					continue;
				if (!string.Equals(GetWindowGroupKey(window), scene.Key, StringComparison.OrdinalIgnoreCase))
					continue;

				scene.Add(window);
				Log.Scene("Adopted orphaned window of the same app", scene, window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));
			}
		}

		private async void WindowsManager_WindowCreated(IWindow window, bool firstCreate)
		{
			// A new window opens wherever its app put it; that display's instance takes it.
			if (!IsOnMyMonitor(window))
				return;

			SwitchToSceneByNewWindow(window).SafeFireAndForget();
		}

		// How often an activation that lost the race against a running transition is retried.
		private const int SwitchRetries = 3;
		private const int SwitchRetryDelayMs = 350;

		private async Task SwitchToSceneByWindow(IWindow window, int attempt = 0)
		{
			// Keep persistent windows (e.g. Teams meeting pop-ups) outside of scene logic.
			if (IsPersistentWindow(window))
			{
				Log.Window("SCENE", "Persistent window, skipping scene logic", window);
				return;
			}

			// Only create/switch scenes for windows that are actually focused, not just shown
			// This prevents scene creation for minimized windows that get Show events without focus
			if (!window.IsFocused)
			{
				Log.Window("SCENE", "Window not focused, skipping scene switch", window);
				return;
			}

			var scene = FindSceneForWindow(window);
			if (scene is null)
			{
				// Window not yet bound to any scene. Before creating a new one, check whether
				// a scene already exists for this process (race: focus event can fire before
				// SwitchToSceneByNewWindow has bound the window). If so, adopt the window into
				// the existing scene to prevent duplicate scenes for the same app.
				var key = GetWindowGroupKey(window);
				var byKey = FindSceneForProcess(key);
				if (byKey is not null)
				{
					byKey.Add(window);
					scene = byKey;
					Log.Scene("Adopted window into existing scene by process key", scene, window);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));
				}
				else
				{
					scene = new Scene(key, window);
					lock (_scenesLock)
						_scenes.Add(scene);
					Log.Scene("Created new scene for window", scene, window);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Created));
					AdoptOrphanWindows(scene);
				}
			}
			else
			{
				Log.Scene("Switching to existing scene", scene, window);
			}

			var switched = AnimatedSwitch != null
				? await AnimatedSwitch(scene)
				: await SwitchTo(scene);

			// The sidebar turns a switch down while a transition or drag is running. The window
			// has the focus all the same — parked, when it came from the taskbar or a link — and
			// nothing else would ever bring its scene on, so ask again once the motion is over.
			if (!switched && !IsCurrentScene(scene) && attempt < SwitchRetries)
			{
				PostToUi(SwitchRetryDelayMs, () =>
				{
					if (!window.IsFocused)
						return;
					// IsCurrentScene(null) means desktop view, so a missing scene is spelled out.
					var now = FindSceneForWindow(window);
					if (now is null || !IsCurrentScene(now))
						SwitchToSceneByWindow(window, attempt + 1).SafeFireAndForget();
				});
			}
		}

		private async Task SwitchToSceneByNewWindow(IWindow window)
		{
			// Keep persistent windows (e.g. Teams meeting pop-ups) outside of scene logic.
			if (IsPersistentWindow(window))
			{
				Log.Window("SCENE", "New persistent window, skipping", window);
				return;
			}

			// Only CREATE or SWITCH scenes for windows that are actually focused, not just
			// created. An unfocused new window still has to be bound to its app's scene if
			// one exists, or it belongs nowhere and stays parked off-screen for good; it
			// just must not drag the stage over to itself.
			if (!window.IsFocused)
			{
				if (FindSceneForWindow(window) is not null)
					return;

				var ownerScene = FindSceneForProcess(GetWindowGroupKey(window));
				if (ownerScene is null)
				{
					// First window of an app that is not focused yet. WinUI 3 apps (WhatsApp,
					// …) and windows that replace their predecessor get the foreground a moment
					// after they appear — or the event arrives before GetForegroundWindow agrees.
					// Dropping it left the window on screen beside the stage with no scene.
					Log.Window("SCENE", "New window not focused, settling", window);
					PostToUi(NewWindowSettleMs, () => SettleUnfocusedNewWindow(window));
					return;
				}

				ownerScene.Add(window);
				Log.Scene("New unfocused window → added to its app's scene", ownerScene, window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(ownerScene, window, ChangeType.Updated));

				// Match the scene it just joined: on stage if that scene is showing, parked
				// with the others if not.
				if (ReferenceEquals(ownerScene, _current))
					WindowStrategy.Show(window);
				else
					WindowStrategy.Hide(window);
				return;
			}

			// Use the group key (process id) consistently to guarantee a new process -> new scene
			var key = GetWindowGroupKey(window);
			var existentScene = FindSceneForProcess(key);
			var scene = existentScene ?? new Scene(key, window);

			if (existentScene is null)
			{
				lock (_scenesLock)
					_scenes.Add(scene);
				Log.Scene("New window → new scene created", scene, window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Created));
				AdoptOrphanWindows(scene);
			}
			else
			{
				scene.Add(window);
				Log.Scene("New window → added to existing scene", scene, window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));
			}

			await SwitchTo(scene).ConfigureAwait(true);
		}

		// Long enough for a window that activates itself after appearing to have done so.
		private const int NewWindowSettleMs = 300;

		/// <summary>
		/// Decides what becomes of a new window that had no focus and no app scene to join when
		/// it appeared: focused by now, or shown in front of the stage → its scene takes the
		/// stage; behind the stage → it gets a scene in the strip and is parked, as an app
		/// launched behind the current one is on macOS.
		/// </summary>
		private void SettleUnfocusedNewWindow(IWindow window)
		{
			if (_suspend || AnimatedSwitchInProgress?.Invoke() == true)
			{
				PostToUi(NewWindowSettleMs, () => SettleUnfocusedNewWindow(window));
				return;
			}

			// Gone again (splash screen), or bound to a scene by some other path meanwhile. A
			// window that was unregistered and registered again has a new tracking instance.
			var tracked = WindowsManager.Windows.FirstOrDefault(w => w.Handle == window.Handle);
			if (tracked is null || FindSceneForWindow(tracked) is not null)
				return;
			window = tracked;
			if (!IsSceneable(window) || !IsOnMyMonitor(window) || window.IsMinimized || !Win32.IsWindowVisible(window.Handle))
				return;

			if (window.IsFocused)
			{
				Log.Window("SCENE", "New window gained focus after appearing", window);
				_lastFocusedWindow = window;
				SwitchToSceneByWindow(window).SafeFireAndForget();
				return;
			}

			// Its app may have got a scene through another window in the meantime.
			var key = GetWindowGroupKey(window);

			// Shown in front of the stage without taking the focus — a tray app (1Password,
			// WhatsApp) brought forward with a plain ShowWindow. The user opened it and is
			// looking at it, so it takes the stage and the previous app goes to the strip.
			if (IsInFrontOfStage(window))
			{
				var target = FindSceneForProcess(key);
				if (target is null)
				{
					target = new Scene(key, window);
					lock (_scenesLock)
						_scenes.Add(target);
					Log.Scene("Window shown in front of the stage → new scene on stage", target, window);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(target, window, ChangeType.Created));
					AdoptOrphanWindows(target);
				}
				else
				{
					target.Add(window);
					Log.Scene("Window shown in front of the stage → its app's scene on stage", target, window);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(target, window, ChangeType.Updated));
				}

				_lastFocusedWindow = window;
				(AnimatedSwitch is { } animated ? animated(target) : SwitchTo(target)).SafeFireAndForget();
				return;
			}

			var scene = FindSceneForProcess(key);
			if (scene is null)
			{
				scene = new Scene(key, window);
				lock (_scenesLock)
					_scenes.Add(scene);
				Log.Scene("Background window → new scene in the strip", scene, window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Created));
			}
			else
			{
				scene.Add(window);
				Log.Scene("Background window → joined its app's scene", scene, window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(scene, window, ChangeType.Updated));
			}

			if (ReferenceEquals(scene, _current))
				WindowStrategy.Show(window);
			else
				WindowStrategy.Hide(window);
		}

		/// <summary>
		/// True when <paramref name="window"/> stacks above every visible window of the stage —
		/// what the user sees in front. In desktop view any window qualifies.
		/// </summary>
		private bool IsInFrontOfStage(IWindow window)
		{
			if (_current is not { } current)
				return true;

			var stage = new HashSet<IntPtr>(current.Windows.Where(w => !w.IsMinimized).Select(w => w.Handle));
			for (var h = Win32.GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = Win32.GetWindow(h, Win32.GW.GW_HWNDNEXT))
			{
				if (h == window.Handle)
					return true;
				if (stage.Contains(h))
					return false;
			}
			return false;
		}

		/// <summary>
		/// Determines if a scene is switched back to shortly after it has been hidden.
		/// This can happen if an app activates one of it's windows after being hidde,
		/// like Microsoft Teams does if there's a small floating window for a current call.
		/// </summary>
		/// <param name="scene"></param>
		/// <returns></returns>
		/// <summary>
		/// Determines if focus changes are happening too rapidly to indicate system vs user interaction
		/// This helps prevent scene switching loops from automatic focus changes
		/// </summary>
		/// <returns></returns>
		private bool IsRapidFocusChange()
		{
			var now = DateTime.Now;
			if ((now - _lastFocusChange).TotalMilliseconds <100) // Less than100ms since last focus change
			{
				Log.Info("FOCUS", "Rapid focus change detected, filtering");
				_lastFocusChange = now;
				return true; // This is a rapid focus change
			}
			_lastFocusChange = now;
			return false;
		}

		private bool _recheckQueued;
		private IWindow? _pendingParkedActivation;

		/// <summary>
		/// One trailing <see cref="ReconcileForeground"/> per burst of focus changes.
		/// <paramref name="parkedActivation"/> is a parked window whose (non-handoff) activation
		/// the burst swallowed: the reconcile pass never acts on parked windows, so it is carried
		/// across explicitly.
		/// </summary>
		private void ScheduleForegroundRecheck(IWindow? parkedActivation)
		{
			_pendingParkedActivation = parkedActivation;
			if (_recheckQueued) return;
			_recheckQueued = true;
			PostToUi(150, () =>
			{
				_recheckQueued = false;
				var pending = _pendingParkedActivation;
				_pendingParkedActivation = null;

				if (pending is not null && pending.IsFocused
					&& OpacityWindowStrategy.TryGetOriginalPosition(pending.Handle, out _, out _))
				{
					_lastFocusedWindow = pending;
					SwitchToSceneByWindow(pending).SafeFireAndForget();
					return;
				}
				ReconcileForeground();
			});
		}

		/// <summary>
		/// The frontmost non-minimized window of <paramref name="scene"/> as it stacks right now.
		/// Only meaningful for the scene on stage — parked windows keep a z-order nobody sees.
		/// </summary>
		private IWindow? TopmostWindowOf(Scene scene)
		{
			var windows = scene.Windows.Where(w => !w.IsMinimized).ToArray();
			if (windows.Length == 0) return null;

			for (var h = Win32.GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = Win32.GetWindow(h, Win32.GW.GW_HWNDNEXT))
			{
				foreach (var w in windows)
					if (w.Handle == h) return w;
			}
			return windows[^1];
		}

		// Long enough for a departure queued just behind the foreground event to be dispatched,
		// short enough that an app bringing its window forward still feels immediate.
		private const int HandoffSettleMs = 80;

		/// <summary>
		/// Decides what a parked window gaining the foreground was, once both events of a
		/// possible handoff are in: Windows passing focus down the z-order (ignored, and focus
		/// is moved to something visible), or the app bringing its window forward (its scene
		/// comes on stage).
		/// </summary>
		private void SettleParkedActivation(IWindow window, WindowsManager.ForegroundChange change)
		{
			if (WindowsManager.WasHandoff(change))
			{
				Log.Window("FOCUS", "Foreground handed to a parked window as the active one left, ignoring", window);
				// Keyboard focus must not stay in a window nobody can see — keys typed there
				// (Alt+F4 included) would land in it.
				RefocusAfterHandoff();
				return;
			}

			// Focus has moved on, or the window is back on stage by some other path.
			if (!window.IsFocused || !OpacityWindowStrategy.TryGetOriginalPosition(window.Handle, out _, out _))
				return;

			Log.Window("FOCUS", "Parked window brought forward by its app", window);
			_lastFocusedWindow = window;
			SwitchToSceneByWindow(window).SafeFireAndForget();
		}

		/// <summary>
		/// After Windows handed the foreground to a parked window: give it to what the user can
		/// see instead — the top window on stage, or the desktop in desktop view.
		/// </summary>
		private void RefocusAfterHandoff()
		{
			if (_suspend || AnimatedSwitchInProgress?.Invoke() == true)
				return;

			// Only while focus is still where the handoff put it; anything since was deliberate.
			var fg = Win32.GetForegroundWindow();
			if (!OpacityWindowStrategy.TryGetOriginalPosition(fg, out _, out _))
				return;

			var target = _current is { } current ? TopmostWindowOf(current)?.Handle ?? IntPtr.Zero : IntPtr.Zero;
			if (target == IntPtr.Zero)
				target = Win32.GetShellWindow();
			if (target == IntPtr.Zero)
				return;

			Log.Info("FOCUS", "Moving focus off a parked window", target);
			Win32Helper.ForceForegroundWindow(target);
		}

		/// <param name="focus">
		/// False brings the scene on stage without activating any of its windows. Used when a
		/// display is set up at startup, so arranging it does not take focus from the display
		/// the user is actually working on.
		/// </param>
		public async Task<bool> SwitchTo(Scene? scene, bool focus = true)
		{
			if (object.Equals(scene, _current))
			{
				Log.Info("SWITCH", $"Already on scene '{scene?.Title}', skipping");
				return false;
			}

			Log.Info("SWITCH", $"SwitchTo START: '{_current?.Title}' → '{scene?.Title ?? "(desktop)"}'");

			IWindow? focusCandidate = null;

			try
			{
				_suspend = true;

				// Determine the window that currently has the keyboard focus (foreground).
				var foregroundHandle = Win32.GetForegroundWindow();

				// When switching to a scene, skip the foreground window (it gets focus handling separately).
				// When switching to desktop (scene=null), hide ALL windows including the foreground.
				var otherWindows = GetSceneableWindows()
					.Except(scene?.Windows ?? Array.Empty<IWindow>())
					.Where(w => scene is null || w.Handle != foregroundHandle)
					.ToArray();

				var prior = _current;
				_current = scene;

				Scene[] scenesSnapshot;
				lock (_scenesLock)
				{
					// Both ends of the switch were on stage just now; the incoming one stays the
					// newest until it leaves again.
					if (prior is not null && _scenes.Contains(prior))
						_lastOnStage[prior] = ++_stageClock;
					if (scene is not null)
						_lastOnStage[scene] = ++_stageClock;
					scenesSnapshot = _scenes.ToArray();
				}
				foreach (var s in scenesSnapshot)
				{
					s.IsSelected = s.Equals(scene);
				}

				// Read the outgoing stacking BEFORE anything is hidden — parking a window
				// off-screen leaves it in the z-chain, but the order is only meaningful while
				// the scene is still the one on screen.
				CaptureZOrder(otherWindows);

				Log.Info("SWITCH", $"Hiding {otherWindows.Length} windows");
				foreach (var o in otherWindows)
				{
					Log.Window("HIDE", "Hiding", o);
					WindowStrategy.Hide(o);
				}

				// Phase2: bring in target-scene windows.
				if (scene is object)
				{
					Log.Frame("SWITCH", $"Show pass START, {scene.Windows.Count()} windows in target scene");
					// Bottom-most first: Show ends in BringWindowToTop, so whichever window is
					// shown last ends up on top. Feeding them in reverse depth order replays the
					// stacking the scene had when it was last on screen.
					foreach (var w in OrderBottomToTop(scene.Windows))
					{
						// Option1: Restore-then-clear for any minimized window in the active scene.
						// A window the user minimized stays minimized: Show below skips iconic
						// windows, so it keeps its taskbar button and never reaches the stage.
						if (w.IsMinimized && !IsUserMinimized(w))
						{
							Log.Window("SHOW", "Restoring minimized", w);
							w.ShowNormal();
						}

						Log.Window("SHOW", "Showing", w);
						// Always clear any previous opacity/click-through for active scene windows
						WindowStrategy.Show(w);
					}
					// Same frame as START means the whole scene was unparked inside one compose
					// and any remaining stagger is downstream of here — the cards, or the apps
					// repainting. A later frame means the pass itself is what splits them.
					Log.Frame("SWITCH", "Show pass END");

					// Determine which window should get focus after restore – pick the last
					// focused window if it belongs to the scene and is not minimised, otherwise
					// the one that was frontmost. Focusing raises a window to the top, so taking
					// the first in list order here would undo the stacking just restored above.
					if (_lastFocusedWindow is object && scene.Windows.Contains(_lastFocusedWindow) && !_lastFocusedWindow.IsMinimized)
						focusCandidate = _lastFocusedWindow;
					else
						focusCandidate = OrderBottomToTop(scene.Windows).LastOrDefault(w => !w.IsMinimized);

					Log.Window("SWITCH", "Focus candidate", focusCandidate ?? scene.Windows.FirstOrDefault());
				}

				CurrentSceneSelectionChanged?.Invoke(this, new CurrentSceneSelectionChangedEventArgs(prior, _current));

				bool anyDesktopView;
				lock (_inDesktopView)
				{
					if (scene is null)
						_inDesktopView.Add(this);
					else
						_inDesktopView.Remove(this);
					anyDesktopView = _inDesktopView.Count > 0;
				}

				if (scene is null)
					_lastScene = prior;
				else
					_lastScene = null;

				if (_hideDesktopIcons)
				{
					if (anyDesktopView)
					{
						Log.Info("DESKTOP", "Showing desktop icons (a display is in desktop view)");
						_desktop.ShowIcons();
					}
					else
					{
						Log.Info("DESKTOP", "Hiding desktop icons (every display shows a scene)");
						_desktop.HideIcons();
					}
				}
			}
			finally
			{
				_suspend = false;

				// Apply focus once suspension lifted
				if (focus && focusCandidate is object)
					focusCandidate.Focus();

				Log.Info("SWITCH", $"SwitchTo END: now on '{_current?.Title ?? "(desktop)"}'");
			}

			return true;
		}

		public Task MoveWindow(Scene sourceScene, IWindow window, Scene targetScene)
		{
			try
			{
				_suspend = true;

				if (sourceScene is null || sourceScene.Equals(targetScene))
					return Task.CompletedTask;

				Log.Window("MOVE", $"Moving from '{sourceScene.Title}' → '{targetScene.Title}'", window);

				sourceScene.Remove(window);
				targetScene.Add(window);

				SceneChanged?.Invoke(this, new SceneChangedEventArgs(sourceScene, window, ChangeType.Updated));
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(targetScene, window, ChangeType.Updated));

				if (!sourceScene.Windows.Any())
				{
					Log.Scene("Source scene empty after move, removing", sourceScene);
					ForgetScene(sourceScene);
					SceneChanged?.Invoke(this, new SceneChangedEventArgs(sourceScene, window, ChangeType.Removed));
				}

				if (targetScene.Equals(_current))
				{
					if (window.IsMinimized)
					{
						Log.Window("MOVE", "Restoring minimized window before showing", window);
						// Dragging a window onto the current scene is the user asking for it
						// back, so it stops counting as deliberately minimized.
						ForgetUserMinimized(window);
						Win32Helper.SetAlpha(window.Handle, 0);
						window.ShowNormal();
					}
					Log.Window("MOVE", "Target is current scene, showing window", window);
					WindowStrategy.Show(window);
					window.Focus();
				}
				else
				{
					Log.Window("MOVE", "Target is not current scene, hiding window", window);
					WindowStrategy.Hide(window);

					// reset window position after move so that the window is back at the starting position on the new scene
					if (window is WindowsWindow w && w.PopLastLocation() is IWindowLocation l)
						Win32.SetWindowPos(window.Handle, IntPtr.Zero, l.X, l.Y,0,0, Win32.SetWindowPosFlags.IgnoreResize);
				}

				return Task.CompletedTask;
			}
			finally
			{
				_suspend = false;
			}
		}

		public async Task MoveWindow(IntPtr handle, Scene targetScene)
		{
			var source = FindSceneForWindow(handle);

			if (source is null || source.Equals(targetScene))
				return;

			var window = source.Windows.First(w => w.Handle == handle);
			await MoveWindow(source, window, targetScene);
		}

		public async Task PopWindowFrom(Scene sourceScene)
		{
			if (sourceScene is null || sourceScene.Equals(_current))
				return;

			// Nothing on stage to pull the window into: desktop view, or the stage's last window
			// just closed and its scene is gone. Dropping a tile there brings its scene on stage,
			// as on macOS — it used to do nothing, and pulling into the removed scene would have
			// lost the window with it.
			var target = _current;
			bool stageLive;
			lock (_scenesLock)
				stageLive = target is not null && _scenes.Contains(target);
			if (target is null || !stageLive)
			{
				Log.Scene("Dropped onto an empty stage, bringing the scene on", sourceScene);
				await SwitchTo(sourceScene).ConfigureAwait(true);
				return;
			}

			var window = sourceScene.Windows.LastOrDefault();

			if (window is object)
			{
				Log.Window("DRAG", $"Pulling window from '{sourceScene.Title}' into '{target.Title}'", window);
				await MoveWindow(sourceScene, window, target).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// Removes a window from its current scene and creates a new scene for it in the sidebar.
		/// The window is hidden (alpha→0). Returns the new scene, or null if the operation was skipped.
		/// </summary>
		public Scene? SeparateWindowToNewScene(IWindow window)
		{
			var source = FindSceneForWindow(window);
			if (source == null || !source.Equals(_current))
			{
				Log.Window("DRAG", "SeparateWindow skipped: not in current scene", window);
				return null;
			}

			if (source.Windows.Count() <= 1)
			{
				Log.Window("DRAG", "SeparateWindow skipped: last window in scene", window);
				return null;
			}

			try
			{
				_suspend = true;

				Log.Window("DRAG", $"Separating from '{source.Title}' into new scene", window);

				source.Remove(window);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(source, window, ChangeType.Updated));

				var newScene = new Scene(GetWindowGroupKey(window), window);
				lock (_scenesLock)
					_scenes.Add(newScene);
				SceneChanged?.Invoke(this, new SceneChangedEventArgs(newScene, window, ChangeType.Created));

				WindowStrategy.Hide(window);

				return newScene;
			}
			finally
			{
				_suspend = false;
			}
		}

		private bool IsSceneable(IWindow w) => !IsPersistentWindow(w) && w.CanLayout && !string.IsNullOrEmpty(w.ProcessFileName) && !string.IsNullOrEmpty(w.Title);

		// ToArray: the display test crosses into user32 per window, and callers enumerate the
		// result more than once.
		private IEnumerable<IWindow> GetSceneableWindows() => WindowsManager.Windows.Where(w => IsSceneable(w) && IsOnMyMonitor(w)).ToArray();

		/// <summary>
		/// The scene whose window sits highest in the z-order — the one the user was last
		/// working with on this display. Used at startup for displays that do not hold the
		/// foreground window, so each one opens on a stage instead of every window at once.
		/// </summary>
		public Scene? FindTopmostScene()
		{
			for (var h = Win32.GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = Win32.GetWindow(h, Win32.GW.GW_HWNDNEXT))
			{
				if (Win32.IsIconic(h))
					continue;
				if (FindSceneForWindow(h) is Scene scene)
					return scene;
			}
			return null;
		}

		public IEnumerable<Scene> GetScenes()
		{
			lock (_scenesLock)
			{
				if (_scenes.Count == 0)
				{
					var restorePath = App.RestoreScenesPath;
					if (SceneSnapshot.CarryOver is { } carried)
					{
						_scenes = RestoreScenesFromSnapshot(carried) ?? new List<Scene>();
					}
					else if (restorePath != null)
					{
						_scenes = RestoreScenesFromSnapshot(restorePath) ?? new List<Scene>();
						UpdateService.CleanupStagingFolder();
					}
					else if (App.SessionScenesPath is { } sessionPath)
					{
						// The layout the last run quit with.
						_scenes = RestoreScenesFromSnapshot(sessionPath) ?? new List<Scene>();
					}

					if (_scenes.Count == 0)
					{
						_scenes = GetSceneableWindows()
							// Include all windows during initial startup (including minimized ones) for automatic scene population
							.Where(w => Win32.IsWindowVisible(w.Handle) || w.IsMinimized)
							.GroupBy(GetWindowGroupKey)
							.Select(group => new Scene(group.Key, group.ToArray()))
							.ToList();
					}

					Log.Info("STARTUP", $"Initial scenes: {_scenes.Count}");
					foreach (var scene in _scenes)
						Log.Scene("Initial scene", scene);
				}

				return _scenes.ToList();
			}
		}

		public SceneSnapshot.Snapshot CreateSnapshot()
		{
			Scene[] snap;
			Scene? currentScene;
			lock (_scenesLock)
			{
				snap = _scenes?.ToArray() ?? Array.Empty<Scene>();
				currentScene = _current;
			}

			var activeHandles = currentScene?.Windows.Select(w => (long)w.Handle).ToArray() ?? Array.Empty<long>();
			var sceneEntries = snap.Select(s => new SceneSnapshot.SceneEntry(
				s.Key,
				s.Windows.Select(w => (long)w.Handle).ToArray(),
				s.Windows.Select(w => w.ProcessId).ToArray()
			)).ToArray();

			return new SceneSnapshot.Snapshot(activeHandles, sceneEntries);
		}

		private List<Scene>? RestoreScenesFromSnapshot(string path)
		{
			var snapshot = SceneSnapshot.Load(path);
			if (snapshot is null)
			{
				Log.Info("STARTUP", "Scene snapshot missing or corrupt, falling back to default grouping");
				return null;
			}

			return RestoreScenesFromSnapshot(snapshot);
		}

		private List<Scene>? RestoreScenesFromSnapshot(SceneSnapshot.Snapshot snapshot)
		{
			var allWindows = GetSceneableWindows()
				.Where(w => Win32.IsWindowVisible(w.Handle) || w.IsMinimized)
				.ToDictionary(w => (long)w.Handle);

			var claimed = new HashSet<long>();
			var scenes = new List<Scene>();

			foreach (var entry in snapshot.Scenes)
			{
				// A handle whose window now belongs to another process was reused by Windows
				// (the session file survived a reboot); it is not the window that was saved.
				bool SameProcess(int i) => entry.ProcessIds is not { } pids || i >= pids.Length
					|| allWindows[entry.Handles[i]].ProcessId == pids[i];

				var validWindows = entry.Handles
					.Select((h, i) => (h, i))
					.Where(x => Win32.IsWindow((IntPtr)x.h) && allWindows.ContainsKey(x.h) && !claimed.Contains(x.h) && SameProcess(x.i))
					.Select(x => { claimed.Add(x.h); return allWindows[x.h]; })
					.ToArray();

				if (validWindows.Length > 0)
				{
					scenes.Add(new Scene(entry.Key, validWindows));
					Log.Info("STARTUP", $"Restored scene '{entry.Key}' with {validWindows.Length}/{entry.Handles.Length} windows");
				}
			}

			// Unclaimed windows get default PID grouping
			var unclaimed = allWindows
				.Where(kv => !claimed.Contains(kv.Key))
				.Select(kv => kv.Value);

			var defaultScenes = unclaimed
				.GroupBy(GetWindowGroupKey)
				.Select(g => new Scene(g.Key, g.ToArray()));
			scenes.AddRange(defaultScenes);

			Log.Info("STARTUP", $"Scene restore complete: {scenes.Count} scenes ({claimed.Count} windows restored)");
			return scenes.Count > 0 ? scenes : null;
		}

		public bool IsCurrentScene(Scene? scene) => object.Equals(scene, _current);

		/// <summary>
		/// True when a window of <paramref name="scene"/> is already on screen — not parked, not
		/// minimized. That happens when the user brought it there without the strip: dragged over
		/// from another display, shown by its tray icon. Nothing is left to fly in, and flying a
		/// card over it would only cover the window the user is looking at.
		/// </summary>
		public bool IsOnScreen(Scene scene) => scene.Windows.Any(w =>
			!w.IsMinimized
			&& Win32.IsWindowVisible(w.Handle)
			&& !OpacityWindowStrategy.TryGetOriginalPosition(w.Handle, out _, out _));

		/// <summary>
		/// Re-stows a window off-screen via the active strategy. Used to return a
		/// dragged tray window to its parked state when a sidebar drag is cancelled,
		/// so it stays hidden on stage while the live tile keeps capturing it.
		/// </summary>
		#region Z-order preservation
		// Depth of a window the last time its scene was on screen: 0 = frontmost, larger =
		// further back. Without this a switch rebuilt the stack from Scene.Windows list order,
		// so two overlapping windows swapped which one was on top every time.
		private readonly Dictionary<IntPtr, int> _zDepth = new();
		private readonly object _zDepthLock = new();

		/// <summary>
		/// Records how deep each of <paramref name="windows"/> currently sits in the desktop
		/// z-chain. Walks the chain once front-to-back and stops as soon as every window of
		/// interest has been placed, so the cost is bounded by the windows above the last one.
		/// </summary>
		private void CaptureZOrder(IReadOnlyCollection<IWindow> windows)
		{
			if (windows.Count == 0) return;

			var wanted = new HashSet<IntPtr>(windows.Select(w => w.Handle));
			var found = new Dictionary<IntPtr, int>(wanted.Count);
			var depth = 0;

			for (var h = Win32.GetTopWindow(IntPtr.Zero);
				h != IntPtr.Zero && found.Count < wanted.Count;
				h = Win32.GetWindow(h, Win32.GW.GW_HWNDNEXT))
			{
				if (wanted.Contains(h))
					found[h] = depth++;
			}

			if (found.Count == 0) return;

			lock (_zDepthLock)
			{
				foreach (var (handle, d) in found)
					_zDepth[handle] = d;
			}
		}

		/// <summary>
		/// The scene's windows ordered back-to-front, so a caller that brings each one to the
		/// top in turn ends with the captured stacking. Windows with no captured depth (never
		/// seen on screen — a brand new window, or one added while the scene was hidden) sort
		/// to the back, behind everything whose position is actually known.
		/// </summary>
		private IEnumerable<IWindow> OrderBottomToTop(IEnumerable<IWindow> windows)
		{
			lock (_zDepthLock)
			{
				return windows
					.OrderByDescending(w => _zDepth.TryGetValue(w.Handle, out var d) ? d : int.MaxValue)
					.ToArray();
			}
		}

		private void ForgetZOrder(IntPtr handle)
		{
			lock (_zDepthLock)
				_zDepth.Remove(handle);
		}
		#endregion

		public void ParkWindow(IWindow window) => WindowStrategy.Hide(window);

		/// <summary>
		/// Restores a parked window to its saved on-stage rect (and full alpha). Counterpart
		/// to <see cref="ParkWindow"/>; used to cancel a stage→tray drag.
		/// </summary>
		public void RestoreWindow(IWindow window) => WindowStrategy.Show(window);

		/// <summary>
		/// Restores a parked window straight to (<paramref name="x"/>, <paramref name="y"/>) in
		/// physical pixels, instead of its saved spot — for a drag that brings it back under
		/// the pointer.
		/// </summary>
		public void RestoreWindowAt(IWindow window, int x, int y)
		{
			OpacityWindowStrategy.SetOriginalPosition(window.Handle, x, y);
			WindowStrategy.Show(window);
		}

		public bool IsDesktopView => _current is null;

		public IEnumerable<IWindow> GetCurrentWindows() => _current?.Windows ?? GetSceneableWindows();

		/// <summary>
		/// Instantly hides all windows in the current scene (alpha→0) without doing a full SwitchTo.
		/// Used by the transition animation so the outgoing placeholder covers the real window.
		/// </summary>
		public void HideCurrentSceneWindows()
		{
			if (_current == null)
			{
				Log.Info("SWITCH", "Pre-hide: no current scene, nothing to hide");
				return;
			}
			var windows = _current.Windows.ToArray();
			Log.Info("SWITCH", $"Pre-hiding {windows.Length} windows in '{_current.Title}' for animation");

			// The animated path parks the outgoing scene here, before SwitchTo runs, so this is
			// the last moment its stacking is readable. Capture it or coming back to this scene
			// finds no depths recorded and falls back to list order.
			CaptureZOrder(windows);

			foreach (var w in windows)
				WindowStrategy.Hide(w);
		}


		/// <summary>
		/// Restores minimized windows in a scene at alpha=0 so they have real screen positions
		/// but are invisible. Prevents the Windows taskbar restore animation on first switch.
		/// </summary>
		public void RestoreMinimizedInvisibly(Scene scene)
		{
			if (scene == null) return;
			foreach (var w in scene.Windows.Where(w => w.IsMinimized && !IsUserMinimized(w)))
			{
				Win32Helper.SetAlpha(w.Handle, 0);
				Log.Window("SWITCH", "Silent restore (minimized→alpha=0)", w);
				w.ShowNormal();
			}
		}

		/// <summary>
		/// Shows desktop icons immediately (used when setting is disabled)
		/// </summary>
		public void ShowDesktopIcons()
		{
			_desktop.ShowIcons();
		}

		/// <summary>
		/// Hides desktop icons immediately (used when setting is enabled)
		/// </summary>
		public void HideDesktopIcons()
		{
			_desktop.HideIcons();
		}

		// Group windows by **process id** instead of the process name so that every
		// newly-launched program (i.e. a new process, even if it shares the same
		// executable name with another instance) gets its **own** scene.
		//
		// This fulfils the requirement that launching a new program should ALWAYS
		// create a separate scene.
		//
		// One at a Time keys each window on its own, so no two windows ever share a scene
		// unless the user groups them by dragging one onto the stage. Every lookup that means
		// "the app's scene" (adoption, orphans, joining the app on stage) then finds only the
		// window's own, which is exactly the One at a Time behaviour.
		private string GetWindowGroupKey(IWindow window) => OneWindowAtATime
			? $"{window.ProcessId}:{window.Handle.ToInt64():X}"
			: window.ProcessId.ToString();

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (!_disposed)
			{
				if (disposing)
				{
					// Already handled by Stop() method which should be called explicitly
					// But ensure cleanup in case Dispose is called directly
					Stop();
				}
				_disposed = true;
			}
		}
	}
}
