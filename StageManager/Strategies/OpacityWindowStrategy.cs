using StageManager.Native.PInvoke;
using StageManager.Native.Window;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;

namespace StageManager.Strategies
{
	/// <summary>
	/// Hides windows by moving them off-screen (out of the visible desktop bounds) so DWM keeps
	/// compositing them and Windows.Graphics.Capture can still produce live frames for the sidebar.
	/// Off-screen positioning is required because WGC captures DWM-composited output (post-alpha) —
	/// the previous alpha=0 trick is invisible to the compositor and would yield transparent frames.
	/// </summary>
	internal class OpacityWindowStrategy : IWindowStrategy
	{
		// Saved on-screen position so Show() can restore the parked window.
		private static readonly Dictionary<IntPtr, (int X, int Y)> _originalPositions = new();

		// Atomic state management
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, System.Threading.SemaphoreSlim> _windowLocks = new();
		private static readonly object _globalLock = new object();

		/// <summary>
		/// Returns the saved on-screen position for a window currently parked off-screen by Hide,
		/// or false if the window has no saved position. Animator uses this to compute the
		/// *intended* incoming-window rect instead of reading the live (off-screen) Bounds.
		/// </summary>
		public static bool TryGetOriginalPosition(IntPtr hWnd, out int x, out int y)
		{
			lock (_globalLock)
			{
				if (_originalPositions.TryGetValue(hWnd, out var pos))
				{
					x = pos.X; y = pos.Y;
					return true;
				}
			}
			x = 0; y = 0;
			return false;
		}

		/// <summary>
		/// Discards the saved position of a window that was repositioned while parked. Hide keeps
		/// the first saved position so a second Hide cannot overwrite it with the off-screen point,
		/// which also means a direct SetWindowPos behind the strategy's back (the sidebar drag moves
		/// the real window to the cursor) leaves a stale entry that Show would restore. Callers that
		/// move a parked window on purpose call this so the next Hide re-reads the live rect.
		/// </summary>
		public static void ForgetOriginalPosition(IntPtr hWnd)
		{
			lock (_globalLock)
			{
				_originalPositions.Remove(hWnd);
			}
		}

		/// <summary>
		/// Replaces where the next Show puts a parked window. For a drag that un-parks a window
		/// under the pointer: restoring to the pre-drag spot first and moving it a frame later
		/// flashed it at its old position.
		/// </summary>
		public static void SetOriginalPosition(IntPtr hWnd, int x, int y)
		{
			lock (_globalLock)
			{
				if (_originalPositions.ContainsKey(hWnd))
					_originalPositions[hWnd] = (x, y);
			}
		}

		/// <summary>
		/// Moves a parked window back to its saved spot without showing or activating it, and
		/// forgets the spot. For a window its app hid while it sat in the strip: when the app
		/// shows it again it should appear where it was, not at the park point.
		/// </summary>
		public static void ReturnHome(IntPtr hWnd)
		{
			lock (_globalLock)
			{
				if (!_originalPositions.Remove(hWnd, out var pos))
					return;
				var (x, y) = OnAnExistingDisplay(hWnd, pos.X, pos.Y);
				Win32.SetWindowPos(hWnd, IntPtr.Zero, x, y, 0, 0,
					Win32.SetWindowPosFlags.IgnoreResize |
					Win32.SetWindowPosFlags.IgnoreZOrder |
					Win32.SetWindowPosFlags.DoNotActivate);
			}
		}

		/// <summary>
		/// Cleans up all per-window state (locks, saved positions) when a window is destroyed.
		/// </summary>
		public static void CleanupWindow(IntPtr hWnd)
		{
			if (_windowLocks.TryRemove(hWnd, out var sem))
			{
				// Acquire before disposing to avoid ObjectDisposedException
				// in Show/Hide if they're mid-operation on this handle.
				sem.Wait();
				sem.Dispose();
			}
			lock (_globalLock)
			{
				_originalPositions.Remove(hWnd);
			}
			// A later window can be given the same handle value; it must not inherit this
			// one's record of which styles are ours to remove.
			Win32Helper.ForgetStyles(hWnd);
		}

		private static (int X, int Y) GetOffScreenPoint()
		{
			// Past the bottom-right of the virtual screen (all monitors combined).
			// Must use PHYSICAL pixels: SetWindowPos takes pixels, but WPF's
			// SystemParameters.VirtualScreen is in DIPs — under >100% scale that DIP
			// value lands back INSIDE the panel (window shows at the bottom-right corner
			// instead of parking). WinForms SystemInformation.VirtualScreen is in the same
			// physical-pixel space as SetWindowPos. +100 keeps a margin off the real edge.
			var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
			return (vs.Right + 100, vs.Bottom + 100);
		}

		private static bool ShouldSkipTransparencyForWindow(IntPtr hWnd)
		{
			if (Win32.IsIconic(hWnd))
				return true;

			if (!Win32.IsWindowVisible(hWnd))
				return true;

			if ((long)Win32.GetWindowExStyleLongPtr(hWnd) == 0)
				return true;

			return false;
		}

		public void Show(IWindow window)
		{
			var hWnd = window.Handle;

			// A minimized window stays exactly as it is. It used to be re-minimized here with
			// SW_SHOWMINIMIZED, which ACTIVATES the window: every switch to a scene holding a
			// user-minimized window handed it the foreground, and a window its app had hidden
			// while minimized (closed to the notification area) was shown again.
			if (ShouldSkipTransparencyForWindow(hWnd))
			{
				Log.Window("OPACITY", "Show SKIPPED (invalid/minimized)", window);
				return;
			}

			var lockSem = _windowLocks.GetOrAdd(hWnd, _ => new System.Threading.SemaphoreSlim(1, 1));

			// Every call below crosses into the owning app's process, and the two that move
			// the window block until its UI thread answers. Each step is stamped so a scene
			// whose windows do not arrive together says which window stalled and on what.
			var tEnter = Stopwatch.GetTimestamp();

			lockSem.Wait();
			var tLock = Stopwatch.GetTimestamp();
			try
			{
				if (ShouldSkipTransparencyForWindow(hWnd))
					return;

				lock (_globalLock)
				{
					if (_originalPositions.TryGetValue(hWnd, out var pos))
					{
						var (x, y) = OnAnExistingDisplay(hWnd, pos.X, pos.Y);
						Win32.SetWindowPos(hWnd, IntPtr.Zero,
							x, y, 0, 0,
							Win32.SetWindowPosFlags.IgnoreResize |
							Win32.SetWindowPosFlags.DoNotActivate);
						_originalPositions.Remove(hWnd);
					}
				}
				var tMove = Stopwatch.GetTimestamp();

				// Clear WS_EX_TRANSPARENT (mouse-through) — UncloakStartupMinimized in
				// WindowsManager sets it on cloaked startup-minimized windows; this is
				// where it gets cleared the first time the window is shown via a scene.
				// Only ever the style we added: an app that is click-through on purpose keeps it.
				Win32Helper.ClearClickThrough(hWnd);
				var tStyle = Stopwatch.GetTimestamp();

				// Drop the layered style we may have added (alpha 0 for a silent restore) —
				// that alone makes the window opaque again. Alpha 255 is not enough: Chromium
				// refuses to paint a window that was forced layered from outside, so leaving
				// the style set hands the user a blank Chrome window. Windows the app made
				// layered itself are not touched.
				Log.Window("OPACITY", "Instant show alpha→255", window);
				Win32Helper.ClearLayered(hWnd);
				var tAlpha = Stopwatch.GetTimestamp();

				window.BringToTop();
				var tRaise = Stopwatch.GetTimestamp();

				// Read the rect back instead of trusting the call. SetWindowPos returning
				// does not mean the owning app has finished processing WM_WINDOWPOSCHANGED,
				// and a window still reading as parked here has not moved yet.
				var rect = new Win32.Rect();
				Win32.GetWindowRect(hWnd, ref rect);

				Log.Frame("SHOWSTEP", $"0x{hWnd.ToInt64():X} lock={Ms(tEnter, tLock)} move={Ms(tLock, tMove)} style={Ms(tMove, tStyle)} alpha={Ms(tStyle, tAlpha)} raise={Ms(tAlpha, tRaise)} total={Ms(tEnter, tRaise)}ms at=({rect.Left},{rect.Top})");
			}
			finally
			{
				lockSem.Release();
			}
		}

		private static string Ms(long from, long to) =>
			((to - from) * 1000.0 / Stopwatch.Frequency).ToString("F2");

		/// <summary>
		/// Where a parked window can go back to. Its saved spot, unless no display shows any of
		/// it any more — the display it lived on was unplugged while it sat in the strip — in
		/// which case it is pulled onto the nearest display that is still there.
		/// </summary>
		private static (int X, int Y) OnAnExistingDisplay(IntPtr hWnd, int x, int y)
		{
			var r = new Win32.Rect();
			if (!Win32.GetWindowRect(hWnd, ref r) || r.Width <= 0 || r.Height <= 0)
				return (x, y);

			var home = new System.Drawing.Rectangle(x, y, r.Width, r.Height);
			foreach (var screen in System.Windows.Forms.Screen.AllScreens)
			{
				if (screen.WorkingArea.IntersectsWith(home))
					return (x, y);
			}

			var wa = System.Windows.Forms.Screen.FromRectangle(home).WorkingArea;
			var cx = Math.Max(wa.Left, Math.Min(x, wa.Right - Math.Min(r.Width, wa.Width)));
			var cy = Math.Max(wa.Top, Math.Min(y, wa.Bottom - Math.Min(r.Height, wa.Height)));
			Log.Info("OPACITY", $"Saved spot ({x},{y}) is on no display any more, restoring at ({cx},{cy})", hWnd);
			return (cx, cy);
		}

		public void Hide(IWindow window)
		{
			var hWnd = window.Handle;

			if (ShouldSkipTransparencyForWindow(hWnd))
			{
				Log.Window("OPACITY", "Hide SKIPPED (invalid/minimized)", window);
				return;
			}

			var lockSem = _windowLocks.GetOrAdd(hWnd, _ => new System.Threading.SemaphoreSlim(1, 1));

			lockSem.Wait();
			try
			{
				if (ShouldSkipTransparencyForWindow(hWnd))
				{
					Log.Window("OPACITY", "Hide SKIPPED (double-check)", window);
					return;
				}

				var point = GetOffScreenPoint();
				Log.Window("OPACITY", $"Move off-screen to ({point.X},{point.Y})", window);
				lock (_globalLock)
				{
					if (!_originalPositions.ContainsKey(hWnd))
					{
						Win32.Rect rect = new Win32.Rect();
						Win32.GetWindowRect(hWnd, ref rect);
						_originalPositions[hWnd] = (rect.Left, rect.Top);
					}
				}

				// IgnoreZOrder matters: hWndInsertAfter of Zero is HWND_TOP, so without it every
				// park yanked the window to the FRONT of the z-chain on its way off-screen.
				// Parking is meant to be invisible, and the stacking a scene had when it was
				// last on screen is read back later to restore it (SceneManager.CaptureZOrder).
				Win32.SetWindowPos(hWnd, IntPtr.Zero,
					point.X, point.Y, 0, 0,
					Win32.SetWindowPosFlags.IgnoreResize |
					Win32.SetWindowPosFlags.IgnoreZOrder |
					Win32.SetWindowPosFlags.DoNotActivate);

				// A parked window must be OPAQUE, and nothing else guarantees it. Callers hide
				// windows with alpha 0 before this runs — UncloakStartupMinimized does it to
				// bring a startup-minimized window back without flashing it, and
				// RestoreMinimizedInvisibly does the same — and only Show ever raises alpha
				// again, which by definition never runs for a window that stays in the tray.
				// Left at 0, WGC (which captures DWM's POST-alpha output, see the class remarks)
				// feeds the sidebar fully transparent frames and the tile renders empty. Alpha
				// is doing no hiding work once the window is off-screen, so drop the layered
				// style we added here, after the move so there is no frame where it is both
				// opaque and still on screen. Removing it rather than setting alpha 255 matters
				// for Chromium, which stops painting while forced layered — the tile would
				// freeze on its last frame. A window that was never ours to make layered is
				// left exactly as its app set it.
				Win32Helper.ClearLayered(hWnd);
			}
			finally
			{
				lockSem.Release();
			}
		}
	}
}
