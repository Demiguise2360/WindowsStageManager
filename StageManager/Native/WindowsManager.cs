using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using StageManager.Helpers;
using StageManager.Native.PInvoke;
using StageManager.Native.Window;

namespace StageManager.Native
{
	public delegate void WindowDelegate(IWindow window);
	public delegate void WindowCreateDelegate(IWindow window, bool firstCreate);
	public delegate void WindowUpdateDelegate(IWindow window, WindowUpdateType type);

	public class WindowsManager : IWindowsManager
	{
		private volatile bool _active;
		private IDictionary<IntPtr, WindowsWindow> _windows;
		private WinEventDelegate _hookDelegate;

		private WindowsWindow? _mouseMoveWindow;
		private readonly object _mouseMoveLock = new object();
		private Win32.HookProc _mouseHook = null!;
		private readonly HashSet<IntPtr> _startupMinimizedHandles = new();
		private readonly List<IntPtr> _winEventHooks = new();
		private IntPtr _mouseHookHandle;

		private IntPtr _currentProcessWindowHandle;
		private int _currentProcessId;
		private long _lastLeftButtonDown;
		// Double-click handling to suppress scene toggle when user double-clicks a desktop item
		private readonly int _doubleClickTime; // system double-click time in ms
		private bool _desktopClickPending;
		private long _desktopClickTime;
		private IntPtr _desktopClickHandle;
		private readonly object _desktopClickLock = new object();
		private long _lastDragEnd = 0L;
		// Last time the user went through the taskbar or the task switcher — see IsRecentUserActivation.
		private long _lastUserActivation = long.MinValue / 2;
		/// <summary>
		/// Raised after a completed < 250 ms left-button click when the desktop (WorkerW/Progman) is the foreground window.
		/// The <see cref="IntPtr"/> argument is the handle of the desktop window that had focus.
		/// </summary>
		public event EventHandler<IntPtr>? DesktopShortClick;

#if DEBUG
		// Set this to true while debugging to dump detailed information about how each window is
		// evaluated for scene eligibility. The output is written via Debug.WriteLine.
		private const bool DEBUG_WINDOW_FILTER = true;

		private static string FormatStyleFlags(Win32.WS style)
		{
			var flags = new List<string>();
			if (style.HasFlag(Win32.WS.WS_SYSMENU)) flags.Add("SYSMENU");
			if (style.HasFlag(Win32.WS.WS_MINIMIZEBOX)) flags.Add("MINBOX");
			if (style.HasFlag(Win32.WS.WS_MAXIMIZEBOX)) flags.Add("MAXBOX");
			if (style.HasFlag(Win32.WS.WS_CAPTION)) flags.Add("CAPTION");
			if (style.HasFlag(Win32.WS.WS_THICKFRAME)) flags.Add("THICKFRAME");
			return string.Join("|", flags);
		}
#endif
		/// <summary>
		/// Notifies when a new window handle was created by the manager
		/// </summary>
		public event WindowCreateDelegate? WindowCreated;
		/// <summary>
		/// Notifies when a handled window was removed by the manager
		/// </summary>
		public event WindowDelegate? WindowDestroyed;
		/// <summary>
		/// Notifies when a handled window was updated by the manager
		/// This is used internally by the workspace manager to apply the update to the window
		/// </summary>
		public event WindowUpdateDelegate? WindowUpdated;

		public IEnumerable<IWindow> Windows => _windows.Values;

		// Device names of the displays a stage runs on, or null for every display. Windows on
		// any other display are never shown or parked by anyone, so nothing may change them.
		private readonly HashSet<string>? _managedDisplays;

		public WindowsManager(IEnumerable<string>? managedDisplays = null)
		{
			_windows = new Dictionary<IntPtr, WindowsWindow>();
			_hookDelegate = new WinEventDelegate(WindowHook);
			_managedDisplays = managedDisplays is null ? null : new HashSet<string>(managedDisplays, StringComparer.OrdinalIgnoreCase);

			_doubleClickTime = (int)Win32.GetDoubleClickTime();
		}

		/// <summary>True when the minimized window's restored rect lies on a display a stage manages.</summary>
		private bool IsOnManagedDisplay(IntPtr hwnd)
		{
			if (_managedDisplays is null)
				return true;

			var wp = new Win32.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<Win32.WINDOWPLACEMENT>() };
			if (!Win32.GetWindowPlacement(hwnd, ref wp))
				return false;
			var n = wp.rcNormalPosition;
			var rect = System.Drawing.Rectangle.FromLTRB(n.Left, n.Top, n.Right, n.Bottom);
			return _managedDisplays.Contains(Screen.FromRectangle(rect).DeviceName);
		}

		// One WindowsManager feeds every per-display SceneManager, and each of them calls
		// Start and Stop. Only the first Start installs the hooks and only the last Stop
		// removes them — a second EnumWindows pass would re-run the startup rescue on
		// windows the first SceneManager has already parked.
		private int _startCount;

		public Task Start()
		{
			if (_startCount++ > 0)
				return Task.CompletedTask;

			_active = true;
			Log.Info("STARTUP", "WindowsManager starting, registering hooks...");

			var currentProcess = Process.GetCurrentProcess();
			_currentProcessId = currentProcess.Id;
			_currentProcessWindowHandle = currentProcess.MainWindowHandle;

			// Enumerate + un-cloak startup-minimized windows BEFORE registering hooks so the
			// forced SW_SHOWNOACTIVATE doesn't echo EVENT_SYSTEM_MINIMIZEEND back into our pipeline.
			Win32.EnumWindows((handle, param) =>
			{
				if (Win32Helper.IsAppWindow(handle) && !IsOwnWindow(handle))
				{
					UncloakStartupMinimized(handle);
					RescueParkedWindow(handle);
					RegisterWindow(handle, false);
				}
				return true;
			}, IntPtr.Zero);

			// DESTROY..HIDE: HIDE is how an app closing to the notification area takes its window
			// away (ShowWindow SW_HIDE, no destroy, no cloak). Without it the window stayed in its
			// scene and the strip kept a frozen tile that clicked through to nothing.
			_winEventHooks.Add(Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_DESTROY, Win32.EVENT_CONSTANTS.EVENT_OBJECT_HIDE, IntPtr.Zero, _hookDelegate, 0, 0, 0));
			_winEventHooks.Add(Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_CLOAKED, Win32.EVENT_CONSTANTS.EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, _hookDelegate, 0, 0, 0));
			_winEventHooks.Add(Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _hookDelegate, 0, 0, 0));
			_winEventHooks.Add(Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZESTART, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _hookDelegate, 0, 0, 0));
			_winEventHooks.Add(Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _hookDelegate, 0, 0, 0));
			_winEventHooks.Add(Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_SYSTEM_SWITCHEND, Win32.EVENT_CONSTANTS.EVENT_SYSTEM_SWITCHEND, IntPtr.Zero, _hookDelegate, 0, 0, 0));
			_winEventHooks.Add(Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_LOCATIONCHANGE, Win32.EVENT_CONSTANTS.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _hookDelegate, 0, 0, 0));
			_winEventHooks.Add(Win32.SetWinEventHook(Win32.EVENT_CONSTANTS.EVENT_OBJECT_NAMECHANGE, Win32.EVENT_CONSTANTS.EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, _hookDelegate, 0, 0, 0));

			_mouseHook = MouseHook;

			var thread = new Thread(() =>
			{
				try
				{
					_mouseHookHandle = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseHook, currentProcess.MainModule!.BaseAddress, 0);
					Application.Run();
				}
				catch (Exception ex)
				{
					Log.Fatal("HOOK", $"Hook thread crashed: {ex}");
				}
			});

			thread.Name = "WindowsManager";
			thread.IsBackground = true;
			thread.Start();

			Log.Info("STARTUP", $"WindowsManager started, tracking {_windows.Count} windows");
			return Task.CompletedTask;
		}

		public void Stop()
		{
			if (_startCount == 0 || --_startCount > 0)
				return;

			_active = false;

			foreach (var hook in _winEventHooks)
			{
				if (hook != IntPtr.Zero)
				{
					try { Win32.UnhookWinEvent(hook); } catch { /* best-effort during shutdown */ }
				}
			}
			_winEventHooks.Clear();

			if (_mouseHookHandle != IntPtr.Zero)
			{
				try { Win32.UnhookWindowsHookEx(_mouseHookHandle); } catch { /* best-effort during shutdown */ }
				_mouseHookHandle = IntPtr.Zero;
			}

			Application.Exit();

			// Restore startup-minimized windows to their original minimized state so the user
			// doesn't find them stuck cloaked at alpha=0 after the app exits.
			foreach (var hwnd in _startupMinimizedHandles)
			{
				try
				{
					Win32Helper.ClearClickThrough(hwnd);
					// Leave no trace on the window: the layered style survives this process,
					// and a Chromium window that keeps it renders blank from here on.
					Win32Helper.ClearLayered(hwnd);
					Win32.ShowWindow(hwnd, Win32.SW.SW_SHOWMINNOACTIVE);
				}
				catch { /* best-effort during shutdown */ }
			}
			_startupMinimizedHandles.Clear();

			// Clean up window event subscriptions to prevent memory leaks
			foreach (var window in _windows.Values)
			{
				window?.ClearEvents();
			}

			// Clear collections to release references
			_windows.Clear();
		}

		// Where a rescued window lands, inset from the work area's top-left, and how far each
		// further one cascades so a run that parked several doesn't stack them all on one spot.
		private const int RescueInsetPx = 48;
		private const int RescueCascadePx = 32;
		private int _rescuedCount;

		/// <summary>
		/// Brings a window back on screen if a previous run died while it was parked off-screen.
		/// <para>
		/// OpacityWindowStrategy hides windows by moving them past the bottom-right of the
		/// virtual screen, and keeps the way home only in a static dictionary — so a crash, or
		/// anything else that skips Stop, takes the sole record of where they belonged with it.
		/// The window survives with a working taskbar button that cannot bring it anywhere
		/// visible, which reads as the app refusing to open.
		/// </para>
		/// <para>
		/// The original rect is gone, so this restores reachability, not layout: the size is
		/// kept and the window is dropped near the work area's top-left.
		/// </para>
		/// </summary>
		private void RescueParkedWindow(IntPtr hwnd)
		{
			// A minimized window reports a placeholder rect near (-32000,-32000), which would
			// read as off-screen. UncloakStartupMinimized has already restored those by now.
			if (Win32.IsIconic(hwnd))
				return;

			var rect = new Win32.Rect();
			if (!Win32.GetWindowRect(hwnd, ref rect))
				return;

			// Same reachability test the drag uses (DragDropManager.IsOnScreen): the park point
			// is past both the right and bottom edges of the virtual screen.
			var virtualScreen = SystemInformation.VirtualScreen;
			if (rect.Left < virtualScreen.Right && rect.Top < virtualScreen.Bottom)
				return;

			var work = Screen.PrimaryScreen?.WorkingArea ?? virtualScreen;
			var offset = RescueInsetPx + _rescuedCount++ * RescueCascadePx;

			Win32.SetWindowPos(hwnd, IntPtr.Zero,
				work.Left + offset, work.Top + offset, 0, 0,
				Win32.SetWindowPosFlags.IgnoreResize |
				Win32.SetWindowPosFlags.IgnoreZOrder |
				Win32.SetWindowPosFlags.DoNotActivate);

			// Moving it back is not enough on its own. A window parked by a run that also went
			// through UncloakStartupMinimized carries alpha 0 and WS_EX_TRANSPARENT, which only
			// OpacityWindowStrategy.Show ever undoes — so without this the rescue would deliver
			// an invisible, click-through window to the middle of the desktop. The run that
			// added them is gone along with its record of doing so, hence force.
			Win32Helper.ClearClickThrough(hwnd, force: true);
			// A window a previous run left layered renders blank in Chromium apps, so the
			// rescue has to undo that style too, not just the position and the alpha.
			Win32Helper.ClearLayered(hwnd, force: true);

			Log.Info("STARTUP", $"Rescued window 0x{hwnd.ToInt64():X} parked off-screen at ({rect.Left},{rect.Top}) by a previous run");
		}

		/// <summary>
		/// True for a window RegisterWindow drops before it reaches the candidacy filter.
		/// Checked up front because the two calls that run before RegisterWindow change the
		/// window — one un-minimizes it at alpha 0, the other moves it — and a window that is
		/// never going to be tracked is one that never gets shown either, so it would be left
		/// restored and invisible with nothing to put it back.
		/// </summary>
		private bool IsOwnWindow(IntPtr handle)
		{
			if (handle == _currentProcessWindowHandle)
				return true;

			Win32.GetWindowThreadProcessId(handle, out var processId);
			return processId == 0 || (int)processId == _currentProcessId;
		}

		private void UncloakStartupMinimized(IntPtr hwnd)
		{
			if (!Win32.IsIconic(hwnd))
				return;

			// Restored here at alpha 0, a window is only ever made visible again by the stage
			// on its display. On a display no stage runs on it stayed invisible and click-through
			// — not minimized any more, so its taskbar button raised nothing — until quit.
			if (!IsOnManagedDisplay(hwnd))
				return;

			try
			{
				// Cloak BEFORE the show call so the window emerges already invisible — no flash,
				// no focus steal, no z-order disruption.
				Win32Helper.SetAlpha(hwnd, 0);
				Win32Helper.SetClickThrough(hwnd);

				// Restore without activation. DWM now composites the window so its thumbnail goes live.
				Win32.ShowWindow(hwnd, Win32.SW.SW_SHOWNOACTIVATE);

				_startupMinimizedHandles.Add(hwnd);
				Log.Info("STARTUP", $"Uncloaked minimized window 0x{hwnd.ToInt64():X} for live preview");
			}
			catch (Exception ex)
			{
				Log.Fatal("STARTUP", $"Failed to uncloak minimized window 0x{hwnd.ToInt64():X}: {ex.Message}");
			}
		}

		/// <summary>
		/// True when the user clicked the taskbar or used the task switcher (Alt+Tab, Win+Tab)
		/// within the last <paramref name="withinMs"/>. A parked window that gains the foreground
		/// right after that was asked for; one that gains it any other way was handed focus by
		/// Windows walking the z-order, and must not pull its scene on stage.
		/// </summary>
		public bool IsRecentUserActivation(int withinMs = 1500) =>
			Environment.TickCount64 - Interlocked.Read(ref _lastUserActivation) <= withinMs;

		private void MarkUserActivation() => Interlocked.Exchange(ref _lastUserActivation, Environment.TickCount64);

		// Shell surfaces the user activates windows through: the taskbar on each display, its
		// thumbnail previews, and the Alt+Tab / Task View hosts (Win10 and Win11 class names).
		private static bool IsActivationSurface(IntPtr hwnd)
		{
			if (hwnd == IntPtr.Zero) return false;
			var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
			var cls = DesktopShellClassifier.GetClassName(root == IntPtr.Zero ? hwnd : root);
			return cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "TaskListThumbnailWnd"
				or "XamlExplorerHostIslandWindow" or "MultitaskingViewFrame" or "TaskSwitcherWnd"
				or "ForegroundStaging";
		}

		/// <summary>
		/// Re-raises <see cref="WindowUpdateType.MoveEnd"/> for a drag that ended without one —
		/// once a drag has been taken over from the OS move loop, Windows never sends MOVESIZEEND
		/// for the real drop. Listeners that settle a window's display need it all the same.
		/// </summary>
		public void NotifyMoveEnd(IWindow window)
		{
			if (_active && _windows.ContainsKey(window.Handle))
				WindowUpdated?.Invoke(window, WindowUpdateType.MoveEnd);
		}

		// A press longer than this is a rubber-band selection or a drag on the desktop, not a click.
		private const long DesktopClickMaxPressMs = 350;
		// Settle time before the click is raised. Long enough for the list view to update its
		// selection (read by the blank-click test) and for another hook's drag-end to call
		// SuppressNextDesktopClick for the same button-up; short enough to feel instant.
		private const int DesktopClickSettleMs = 40;
		private long _lastDesktopClickRaised = long.MinValue / 2;

		/// <summary>Where the last desktop click landed, physical pixels. Read by DesktopShortClick handlers.</summary>
		public System.Drawing.Point LastDesktopClickPoint { get; private set; }

		/// <summary>
		/// Raises <see cref="DesktopShortClick"/> for a button-up on the desktop, almost at once.
		/// <para>
		/// This used to wait out the whole double-click interval (~500 ms) to rule out a double
		/// click — the "reveal desktop is not instant" lag. Now the first click fires right away
		/// and the second click of a double-click is simply dropped, which gives the same result
		/// without the wait: a double-click on blank wallpaper toggles once.
		/// </para>
		/// </summary>
		private void QueueDesktopClick(IntPtr handle, Win32.POINT cursor)
		{
			var now = Environment.TickCount64;
			if (now - _lastDragEnd < 300)
				return; // a drag's mouse-up landing on the desktop behind the sidebar
			if (now - _lastLeftButtonDown > DesktopClickMaxPressMs)
				return;
			if (now - _lastDesktopClickRaised < _doubleClickTime)
				return; // second click of a double-click

			lock (_desktopClickLock)
			{
				_desktopClickPending = true;
				_desktopClickTime = now;
				_desktopClickHandle = handle;
			}

			// Recorded at the button-up, not when the event is handled: the pointer may already be
			// on another display by then, and that must not decide which stage toggles.
			var point = new System.Drawing.Point(cursor.X, cursor.Y);

			Task.Run(async () =>
			{
				await Task.Delay(DesktopClickSettleMs);
				IntPtr target;
				lock (_desktopClickLock)
				{
					if (!_desktopClickPending) return; // SuppressNextDesktopClick got here first
					_desktopClickPending = false;
					target = _desktopClickHandle;
				}
				_lastDesktopClickRaised = Environment.TickCount64;
				LastDesktopClickPoint = point;
				DesktopShortClick?.Invoke(this, target);
			});
		}

		public IWindowsDeferPosHandle DeferWindowsPos(int count)
		{
			var info = Win32.BeginDeferWindowPos(count);
			return new WindowsDeferPosHandle(info);
		}

		private IntPtr MouseHook(int nCode, UIntPtr wParam, IntPtr lParam)
		{
			if (nCode == 0)
			{
				var msg = (uint)wParam;

				if (msg == Win32.WM_LBUTTONDOWN)
				{
					_lastLeftButtonDown = Environment.TickCount64;
				}
				else if (msg == Win32.WM_LBUTTONUP)
				{
					HandleWindowMoveEnd();

					// Detect click on desktop surface using window under cursor
					Win32.GetCursorPos(out var cursorPt);
					var windowUnderCursor = Win32.WindowFromPoint(new System.Drawing.Point(cursorPt.X, cursorPt.Y));

					if (IsActivationSurface(windowUnderCursor))
						MarkUserActivation();

					if (windowUnderCursor != IntPtr.Zero && DesktopShellClassifier.IsDesktopShell(windowUnderCursor))
						QueueDesktopClick(windowUnderCursor, cursorPt);
				}
			}

			return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
		}

		private void WindowHook(IntPtr hWinEventHook, Win32.EVENT_CONSTANTS eventType, IntPtr hwnd, Win32.OBJID idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
		{
			if (!_active)
				return;

			// Alt+Tab ending, or the switcher / taskbar taking the foreground on its way to
			// handing it on — both mean the next activation is the user's choice.
			if (eventType == Win32.EVENT_CONSTANTS.EVENT_SYSTEM_SWITCHEND ||
				(eventType == Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND && IsActivationSurface(hwnd)))
			{
				MarkUserActivation();
			}

			if (EventWindowIsValid(idChild, idObject, hwnd))
			{
				TrackForegroundHandoff(eventType, hwnd, dwmsEventTime);

				switch (eventType)
				{
					case Win32.EVENT_CONSTANTS.EVENT_OBJECT_SHOW:
						RegisterWindow(hwnd);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_OBJECT_DESTROY:
						UnregisterWindow(hwnd);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_OBJECT_HIDE:
						// Read back rather than trusted: the event is queued, and a window that
						// only blinked (hidden and shown again to apply a style) is visible by
						// now and must keep its scene.
						if (_windows.ContainsKey(hwnd) && !Win32.IsWindowVisible(hwnd))
							UpdateWindow(hwnd, WindowUpdateType.Hide);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_OBJECT_CLOAKED:
						UpdateWindow(hwnd, WindowUpdateType.Hide);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_OBJECT_UNCLOAKED:
						UpdateWindow(hwnd, WindowUpdateType.Show);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZESTART:
						UpdateWindow(hwnd, WindowUpdateType.MinimizeStart);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZEEND:
						UpdateWindow(hwnd, WindowUpdateType.MinimizeEnd);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND:
						UpdateWindow(hwnd, WindowUpdateType.Foreground);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZESTART:
						StartWindowMove(hwnd);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MOVESIZEEND:
						EndWindowMove(hwnd);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_OBJECT_LOCATIONCHANGE:
						WindowMove(hwnd);
						break;
					case Win32.EVENT_CONSTANTS.EVENT_OBJECT_NAMECHANGE:
						if (_windows.TryGetValue(hwnd, out var compact) &&
							string.Equals(compact.ProcessFileName, "ms-teams.exe", StringComparison.OrdinalIgnoreCase) &&
							compact.Title.StartsWith("Meeting compact view", StringComparison.OrdinalIgnoreCase))
						{
							Win32Helper.ClearClickThrough(hwnd);
							// Dropped from tracking for good, so nothing else will ever undo the
							// layered style for this window — do it here or not at all.
							Win32Helper.ClearLayered(hwnd);
							UnregisterWindow(hwnd);
						}
						break;
				}
			}
		}

		// An app window leaving while active — minimized, hidden, destroyed, cloaked — makes
		// Windows hand the foreground to the next window down the z-order, parked windows
		// included. That handoff is the one activation nobody asked for. Everything else that
		// activates a window (a link opening in the browser, a notification, a launcher, an app
		// raising its own window) is the app being brought forward, the Windows equivalent of an
		// app activating on macOS. Event times, not arrival times, so a busy queue cannot blur
		// the two.
		private const int ForegroundHandoffMs = 600;

		/// <summary>One foreground change: the window that got it, the one that had it, and when (event time).</summary>
		public readonly record struct ForegroundChange(IntPtr Window, IntPtr Previous, uint Time);

		/// <summary>The foreground change now being (or last) dispatched. UI thread.</summary>
		public ForegroundChange LastForegroundChange { get; private set; }

		private IntPtr _lastForegroundHwnd;
		private IntPtr _lastDepartureHwnd;
		private uint _lastDepartureTime;

		private void TrackForegroundHandoff(Win32.EVENT_CONSTANTS eventType, IntPtr hwnd, uint eventTime)
		{
			switch (eventType)
			{
				case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_FOREGROUND:
					LastForegroundChange = new ForegroundChange(hwnd, _lastForegroundHwnd, eventTime);
					_lastForegroundHwnd = hwnd;
					break;
				case Win32.EVENT_CONSTANTS.EVENT_SYSTEM_MINIMIZESTART:
				case Win32.EVENT_CONSTANTS.EVENT_OBJECT_HIDE:
				case Win32.EVENT_CONSTANTS.EVENT_OBJECT_DESTROY:
				case Win32.EVENT_CONSTANTS.EVENT_OBJECT_CLOAKED:
					// The active window, or the one that was active right before the latest
					// change — its departure may be queued after the foreground event it caused.
					// Tracked app windows only: a launcher (Start, search, PowerToys Run), the
					// notification centre or a dialog also vanishes the moment the app it opened
					// activates, and that activation is the user's choice, not a handoff. Runs
					// before the event unregisters the window, so it is still tracked here.
					if (hwnd != IntPtr.Zero && _windows.ContainsKey(hwnd)
						&& (hwnd == _lastForegroundHwnd || hwnd == LastForegroundChange.Previous))
					{
						_lastDepartureHwnd = hwnd;
						_lastDepartureTime = eventTime;
					}
					break;
			}
		}

		/// <summary>
		/// True when <paramref name="change"/> was Windows handing the foreground on because the
		/// window that had it was minimized, hidden, closed or cloaked — within
		/// <see cref="ForegroundHandoffMs"/> on either side, since the two events are not
		/// guaranteed to be queued in the order they happened. Ask a moment after the change, so
		/// a departure queued behind it has arrived.
		/// </summary>
		public bool WasHandoff(ForegroundChange change) =>
			change.Previous != IntPtr.Zero
			&& _lastDepartureHwnd == change.Previous
			&& Math.Abs(unchecked((int)(_lastDepartureTime - change.Time))) <= ForegroundHandoffMs;

		private bool EventWindowIsValid(int idChild, Win32.OBJID idObject, IntPtr hwnd)
		{
			return idChild == Win32.CHILDID_SELF && idObject == Win32.OBJID.OBJID_WINDOW && hwnd != IntPtr.Zero;
		}

		/// <summary>
		/// Nudge (and if oversized, shrink) a window so its whole rect fits inside
		/// the work area of the monitor it is nearest to. Used on first registration
		/// of a runtime-created window so it can never appear partly/fully off-screen.
		/// WorkingArea and SetWindowPos share the same physical-pixel space.
		/// </summary>
		private static void EnsureWindowOnScreen(WindowsWindow window)
		{
			var handle = window.Handle;
			// Leave minimized/maximized windows to the OS; their rects aren't a
			// normal restorable position.
			if (Win32.IsIconic(handle) || Win32.IsZoomed(handle))
				return;

			Win32.Rect rect = new Win32.Rect();
			if (!Win32.GetWindowRect(handle, ref rect))
				return;

			var wa = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
			int w = Math.Min(rect.Width, wa.Width);
			int h = Math.Min(rect.Height, wa.Height);
			int x = Math.Max(wa.Left, Math.Min(rect.Left, wa.Right - w));
			int y = Math.Max(wa.Top, Math.Min(rect.Top, wa.Bottom - h));

			if (x == rect.Left && y == rect.Top && w == rect.Width && h == rect.Height)
				return;

			Log.Window("ONSCREEN", $"Clamp ({rect.Left},{rect.Top},{rect.Width}x{rect.Height})→({x},{y},{w}x{h})", window);
			Win32.SetWindowPos(handle, IntPtr.Zero, x, y, w, h,
				Win32.SetWindowPosFlags.DoNotActivate | Win32.SetWindowPosFlags.IgnoreZOrder);
		}

		private void RegisterWindow(IntPtr handle, bool emitEvent = true)
		{
			if (!_active)
				return;

			if (handle == _currentProcessWindowHandle)
				return;

			if (!_windows.ContainsKey(handle))
			{
				var window = new WindowsWindow(handle);

				if (window.ProcessId < 0 || window.ProcessId == _currentProcessId)
					return;

#if DEBUG
				if (DEBUG_WINDOW_FILTER)
				{
					var preStyle = Win32.GetWindowStyleLongPtr(handle);
					Debug.WriteLine($"[WindowFilter] PRE     0x{((long)preStyle):X8} [{FormatStyleFlags(preStyle)}] {window}");
				}
#endif
				bool candidate = window.IsCandidate();

#if DEBUG
				if (DEBUG_WINDOW_FILTER)
				{
					Debug.WriteLine($"[WindowFilter] {(candidate ? "ACCEPT" : "REJECT")} {window}");
				}
#endif

				if (candidate)
				{
					_windows[handle] = window;
					Log.Window("TRACK", "Registered", window);

					if (emitEvent)
					{
						// A freshly-launched window keeps whatever rect the app
						// restored — which can be fully off-screen (apps like Chrome
						// persist their last position, and we park hidden windows
						// off-screen, so the saved position is off-screen). Pull it
						// back so all four corners sit inside the work area before
						// the scene shows it.
						EnsureWindowOnScreen(window);
						HandleWindowAdd(window, true);
					}
				}
			}
		}

		private void UnregisterWindow(IntPtr handle)
		{
			if (!_active)
				return;

			if (_windows.ContainsKey(handle))
			{
				var window = _windows[handle];
				Log.Window("TRACK", "Unregistered", window);
				_windows.Remove(handle);
				HandleWindowRemove(window);
			}
		}

		private void UpdateWindow(IntPtr handle, WindowUpdateType type)
		{
			if (!_active)
				return;

			if (type == WindowUpdateType.Show && _windows.ContainsKey(handle))
			{
				var window = _windows[handle];
				WindowUpdated?.Invoke(window, type);
			}
			else if (type == WindowUpdateType.Show)
			{
				RegisterWindow(handle);
			}
			else if (type == WindowUpdateType.Hide && _windows.ContainsKey(handle))
			{
				var window = _windows[handle];
				if (!window.DidManualHide)
				{
					UnregisterWindow(handle);
				}
				else
				{
					WindowUpdated?.Invoke(window, type);
				}
			}
			else if (_windows.ContainsKey(handle))
			{
				var window = _windows[handle];
				WindowUpdated?.Invoke(window, type);
			}
		}

		private void StartWindowMove(IntPtr handle)
		{
			if (!_active)
				return;

			if (_windows.ContainsKey(handle))
			{
				var window = _windows[handle];
				window.StoreLastLocation();

				HandleWindowMoveStart(window);
				WindowUpdated?.Invoke(window, WindowUpdateType.MoveStart);
			}
		}

		private void EndWindowMove(IntPtr handle)
		{
			if (!_active)
				return;

			if (_windows.ContainsKey(handle))
			{
				var window = _windows[handle];

				HandleWindowMoveEnd();
				WindowUpdated?.Invoke(window, WindowUpdateType.MoveEnd);
			}
		}

		private void WindowMove(IntPtr handle)
		{
			if (!_active)
				return;

			if (_mouseMoveWindow != null && _windows.ContainsKey(handle))
			{
				var window = _windows[handle];
				if (_mouseMoveWindow == window)
					WindowUpdated?.Invoke(window, WindowUpdateType.Move);
			}
		}

		private void HandleWindowMoveStart(WindowsWindow window)
		{
			if (!_active)
				return;

			if (_mouseMoveWindow != null)
				_mouseMoveWindow.IsMouseMoving = false;

			_mouseMoveWindow = window;
			window.IsMouseMoving = true;
		}

		/// <summary>
		/// Marks the button-up now being processed as the end of a drag, so it is not also
		/// read as a click on the desktop behind the sidebar.
		/// <para>
		/// Clearing <c>_desktopClickPending</c> matters as much as stamping the timestamp:
		/// MouseHook queues the pending click on WM_LBUTTONUP and only fires it a
		/// double-click interval later, and that delayed check never re-reads
		/// <c>_lastDragEnd</c>. A caller reacting to the same button-up from a different hook
		/// can therefore arrive after the click is already queued — the timestamp alone would
		/// come too late to stop it.
		/// </para>
		/// </summary>
		public void SuppressNextDesktopClick()
		{
			_lastDragEnd = Environment.TickCount64;
			lock (_desktopClickLock)
				_desktopClickPending = false;
		}

		private void HandleWindowMoveEnd()
		{
			if (!_active)
				return;

			lock (_mouseMoveLock)
			{
				if (_mouseMoveWindow != null)
				{
					var window = _mouseMoveWindow;
					_mouseMoveWindow = null;
					_lastDragEnd = Environment.TickCount64;

					window.IsMouseMoving = false;
				}
			}
		}

		private void HandleWindowAdd(IWindow window, bool firstCreate)
		{
			if (!_active)
				return;

			WindowCreated?.Invoke(window, firstCreate);
		}

		private void HandleWindowRemove(IWindow window)
		{
			if (!_active)
				return;

			WindowDestroyed?.Invoke(window);
		}
	}
}
