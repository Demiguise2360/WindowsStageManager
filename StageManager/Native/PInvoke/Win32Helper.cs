using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace StageManager.Native.PInvoke
{
    public static class Win32Helper
    {

        public static void QuitApplication(IntPtr hwnd)
        {
            Win32.SendNotifyMessage(hwnd, Win32.WM_SYSCOMMAND, Win32.SC_CLOSE, IntPtr.Zero);
        }

        public static bool IsCloaked(IntPtr hwnd)
        {
            bool isCloaked;
            var attr = Win32.DwmGetWindowAttribute(hwnd, (int)Win32.DwmWindowAttribute.DWMWA_CLOAKED, out isCloaked, Marshal.SizeOf(typeof(bool)));
            return isCloaked;
        }

        public static bool IsAppWindow(IntPtr hwnd)
        {
            // A minimized window keeps WS_VISIBLE — only ShowWindow(SW_HIDE) clears it — so an
            // "|| IsIconic" here never admitted an ordinary minimized window that the visibility
            // test had not already admitted. What it did admit is windows an app deliberately
            // hid: closing to the notification area leaves the window minimized AND hidden, and
            // that combination arrived as a scene with a blank tray tile for an app the user had
            // quit. WindowsManager.Start also un-minimizes whatever passes this test, so it
            // brought the hidden window back on top of tiling it.
            return Win32.IsWindowVisible(hwnd) &&
                   !Win32.GetWindowExStyleLongPtr(hwnd).HasFlag(Win32.WS_EX.WS_EX_NOACTIVATE) &&
                   !Win32.GetWindowStyleLongPtr(hwnd).HasFlag(Win32.WS.WS_CHILD);
        }

        public static bool IsAltTabWindow(IntPtr hWnd)
        {
            var exStyle = Win32.GetWindowExStyleLongPtr(hWnd);
            if (exStyle.HasFlag(Win32.WS_EX.WS_EX_TOOLWINDOW) ||
                Win32.GetWindow(hWnd, Win32.GW.GW_OWNER) != IntPtr.Zero)
            {
                return false;
            }
            return true;
        }

        // Windows whose WS_EX_LAYERED / WS_EX_TRANSPARENT this process added. Only these may
        // have the style taken off again: an app that is layered or click-through by design
        // (WPF AllowsTransparency, UpdateLayeredWindow per-pixel alpha, transparent Electron
        // windows) renders black or not at all once either is stripped — and one call to
        // SetLayeredWindowAttributes is enough to make its next UpdateLayeredWindow fail.
        private static readonly HashSet<IntPtr> s_layeredByUs = new();
        private static readonly HashSet<IntPtr> s_clickThroughByUs = new();
        private static readonly object s_styleLock = new();

        /// <summary>
        /// Makes the window layered if it is not already, then applies the given alpha. A window
        /// the app itself made layered is left alone: its alpha is the app's business, and
        /// setting it would break per-pixel-alpha rendering for good.
        /// </summary>
        public static void SetAlpha(IntPtr hWnd, byte alpha)
        {
            var exStyle = Win32.GetWindowExStyleLongPtr(hWnd);
            lock (s_styleLock)
            {
                if (!exStyle.HasFlag(Win32.WS_EX.WS_EX_LAYERED))
                {
                    Win32.SetWindowStyleExLongPtr(hWnd, exStyle | Win32.WS_EX.WS_EX_LAYERED);
                    s_layeredByUs.Add(hWnd);
                }
                else if (!s_layeredByUs.Contains(hWnd))
                {
                    return;
                }
            }
            Win32.SetLayeredWindowAttributes(hWnd, 0, alpha, Win32.LWA_ALPHA);
        }

        /// <summary>
        /// Removes the WS_EX_LAYERED this process added, which also makes the window fully
        /// opaque again. Must be called wherever a window is handed back to the user, because
        /// the style outlives this process: Chromium renders through DirectComposition and a
        /// window forced layered from outside falls back to a redirection surface it never
        /// paints into, so a Chrome window left with the style set comes up blank — and stays
        /// blank after Stage Manager exits.
        /// <para>
        /// <paramref name="force"/> also clears a style this run did not add. Only for the
        /// crash rescue, where a previous run's additions can no longer be told apart.
        /// </para>
        /// </summary>
        public static void ClearLayered(IntPtr hWnd, bool force = false)
        {
            bool ours;
            lock (s_styleLock)
                ours = s_layeredByUs.Remove(hWnd);
            if (!ours && !force)
                return;

            var exStyle = Win32.GetWindowExStyleLongPtr(hWnd);
            if (!exStyle.HasFlag(Win32.WS_EX.WS_EX_LAYERED))
                return;

            // Opaque first: a window that keeps painting through the redirection surface for a
            // frame before the style change lands must not flash at the alpha it was left on.
            Win32.SetLayeredWindowAttributes(hWnd, 0, 255, Win32.LWA_ALPHA);
            Win32.SetWindowStyleExLongPtr(hWnd, exStyle & ~Win32.WS_EX.WS_EX_LAYERED);

            // The style change only reaches the frame on the next SetWindowPos with
            // FrameChanged; without it the window keeps rendering through the stale path.
            Win32.SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SetWindowPosFlags.FrameChanged |
                Win32.SetWindowPosFlags.IgnoreMove |
                Win32.SetWindowPosFlags.IgnoreResize |
                Win32.SetWindowPosFlags.IgnoreZOrder |
                Win32.SetWindowPosFlags.DoNotActivate);
        }

        /// <summary>Makes the window click-through, remembering that the style is ours to remove.</summary>
        public static void SetClickThrough(IntPtr hWnd)
        {
            var exStyle = Win32.GetWindowExStyleLongPtr(hWnd);
            if (exStyle.HasFlag(Win32.WS_EX.WS_EX_TRANSPARENT))
                return;
            lock (s_styleLock)
                s_clickThroughByUs.Add(hWnd);
            Win32.SetWindowStyleExLongPtr(hWnd, exStyle | Win32.WS_EX.WS_EX_TRANSPARENT);
        }

        /// <summary>
        /// Undoes <see cref="SetClickThrough"/>. A window that is click-through by its own design
        /// keeps the style unless <paramref name="force"/> (crash rescue) says otherwise.
        /// </summary>
        public static void ClearClickThrough(IntPtr hWnd, bool force = false)
        {
            bool ours;
            lock (s_styleLock)
                ours = s_clickThroughByUs.Remove(hWnd);
            if (!ours && !force)
                return;

            var exStyle = Win32.GetWindowExStyleLongPtr(hWnd);
            if (exStyle.HasFlag(Win32.WS_EX.WS_EX_TRANSPARENT))
                Win32.SetWindowStyleExLongPtr(hWnd, exStyle & ~Win32.WS_EX.WS_EX_TRANSPARENT);
        }

        /// <summary>Drops the ownership records of a window that no longer exists.</summary>
        public static void ForgetStyles(IntPtr hWnd)
        {
            lock (s_styleLock)
            {
                s_layeredByUs.Remove(hWnd);
                s_clickThroughByUs.Remove(hWnd);
            }
        }

        public static void ForceForegroundWindow(IntPtr hWnd)
        {
            FocusStealer.Steal(hWnd);
        }
    }
}
