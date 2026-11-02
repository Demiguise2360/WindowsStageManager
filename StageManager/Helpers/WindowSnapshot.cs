using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using StageManager.Native.PInvoke;

namespace StageManager.Helpers
{
	/// <summary>
	/// Still image of a window, for places a live capture can't be hosted — the "More" list is
	/// a transparent popup, and a composition child HWND does not render inside one.
	/// <para>
	/// PrintWindow with PW_RENDERFULLCONTENT reads DWM's copy of the window, so it works for a
	/// window parked off-screen (which is where every window in the More list is). Safe to call
	/// off the UI thread; the result is frozen.
	/// </para>
	/// </summary>
	internal static class WindowSnapshot
	{
		private const uint PW_RENDERFULLCONTENT = 0x2;

		/// <summary>
		/// A snapshot scaled to <paramref name="maxWidth"/> pixels wide, or null when the window
		/// can't be read: minimized, refusing capture (comes back black), or gone.
		/// </summary>
		public static BitmapSource? Capture(IntPtr hwnd, int maxWidth)
		{
			try
			{
				if (hwnd == IntPtr.Zero || Win32.IsIconic(hwnd))
					return null;

				var rect = new Win32.Rect();
				if (!Win32.GetWindowRect(hwnd, ref rect) || rect.Width <= 0 || rect.Height <= 0)
					return null;

				using var full = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
				using (var g = Graphics.FromImage(full))
				{
					var hdc = g.GetHdc();
					try
					{
						if (!PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT))
							return null;
					}
					finally
					{
						g.ReleaseHdc(hdc);
					}
				}

				if (IsBlank(full))
					return null;

				var scale = Math.Min(1.0, maxWidth / (double)rect.Width);
				int w = Math.Max(1, (int)(rect.Width * scale));
				int h = Math.Max(1, (int)(rect.Height * scale));

				using var small = new Bitmap(w, h, PixelFormat.Format32bppArgb);
				using (var g = Graphics.FromImage(small))
				{
					g.InterpolationMode = InterpolationMode.HighQualityBicubic;
					g.PixelOffsetMode = PixelOffsetMode.HighQuality;
					g.DrawImage(full, 0, 0, w, h);
				}

				var hBitmap = small.GetHbitmap();
				try
				{
					var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
						hBitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
					source.Freeze();
					return source;
				}
				finally
				{
					DeleteObject(hBitmap);
				}
			}
			catch (Exception ex)
			{
				Log.Info("SNAPSHOT", $"Snapshot of 0x{hwnd:X} failed: {ex.Message}");
				return null;
			}
		}

		// A window that refuses capture prints as solid black. Sample a sparse grid rather than
		// every pixel — a real window is never uniformly black at all of them.
		private static bool IsBlank(Bitmap bmp)
		{
			const int steps = 12;
			for (int yi = 1; yi < steps; yi++)
			for (int xi = 1; xi < steps; xi++)
			{
				var c = bmp.GetPixel(bmp.Width * xi / steps, bmp.Height * yi / steps);
				if (c.A > 0 && (c.R > 8 || c.G > 8 || c.B > 8))
					return false;
			}
			return true;
		}

		[DllImport("user32.dll")]
		private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

		[DllImport("gdi32.dll")]
		private static extern bool DeleteObject(IntPtr hObject);
	}
}
