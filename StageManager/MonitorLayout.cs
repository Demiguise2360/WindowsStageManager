using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Screen = System.Windows.Forms.Screen;

namespace StageManager
{
	/// <summary>
	/// Display geometry shared by the sidebars and the startup picker.
	/// <para>
	/// Everything here comes from WinForms <see cref="Screen"/>, which reports physical pixels
	/// in the same space as GetWindowRect / SetWindowPos. WPF positions windows in DIPs; the
	/// process is system-DPI aware (no per-monitor manifest), so one factor converts between
	/// the two on every display.
	/// </para>
	/// </summary>
	internal static class MonitorLayout
	{
		/// <summary>
		/// All displays, primary first, then left-to-right, top-to-bottom. Stable across calls,
		/// so the index doubles as a display's position in the picker.
		/// </summary>
		public static Screen[] Ordered() => Screen.AllScreens
			.OrderByDescending(s => s.Primary)
			.ThenBy(s => s.Bounds.X)
			.ThenBy(s => s.Bounds.Y)
			.ToArray();

		public static Screen? Find(string deviceName) =>
			Screen.AllScreens.FirstOrDefault(s => string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

		/// <summary>Physical pixels per DIP at system DPI.</summary>
		public static double PixelsPerDip
		{
			get
			{
				double px = Screen.PrimaryScreen?.Bounds.Height ?? 0;
				double dip = SystemParameters.PrimaryScreenHeight;
				return px > 0 && dip > 0 ? px / dip : 1.0;
			}
		}

		public static Rect ToDip(System.Drawing.Rectangle r)
		{
			var s = PixelsPerDip;
			return new Rect(r.X / s, r.Y / s, r.Width / s, r.Height / s);
		}

		/// <summary>
		/// The number Windows shows for a display in Settings, taken from the trailing digits of
		/// its device name (<c>\\.\DISPLAY2</c> → 2). Falls back to the picker order.
		/// </summary>
		public static string Number(Screen screen)
		{
			var digits = new string(screen.DeviceName.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
			if (digits.Length > 0)
				return digits;
			return (Array.FindIndex(Ordered(), s => s.DeviceName == screen.DeviceName) + 1).ToString();
		}

		public static string Describe(Screen screen)
		{
			var b = screen.Bounds;
			var primary = screen.Primary ? " · primary" : "";
			return $"Display {Number(screen)} — {b.Width} × {b.Height}{primary}";
		}

		/// <summary>
		/// Shows each display's number large in its top-left corner, so a "Display 2" in a list
		/// can be matched to a physical screen. Close the returned windows to remove them.
		/// </summary>
		public static List<Window> ShowNumberBadges()
		{
			var badges = new List<Window>();
			foreach (var screen in Ordered())
			{
				var bounds = ToDip(screen.Bounds);
				var border = new Border
				{
					CornerRadius = new CornerRadius(14),
					Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x1E, 0x1E, 0x1E)),
					BorderThickness = new Thickness(screen.Primary ? 3 : 0),
					Padding = new Thickness(34, 6, 34, 14),
					Child = new TextBlock
					{
						Text = Number(screen),
						FontSize = 96,
						FontWeight = FontWeights.SemiBold,
						Foreground = Brushes.White,
					},
				};
				border.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");

				var badge = new Window
				{
					WindowStyle = WindowStyle.None,
					AllowsTransparency = true,
					Background = Brushes.Transparent,
					ShowInTaskbar = false,
					ShowActivated = false,
					Topmost = true,
					ResizeMode = ResizeMode.NoResize,
					SizeToContent = SizeToContent.WidthAndHeight,
					IsHitTestVisible = false,
					Left = bounds.Left + 40,
					Top = bounds.Top + 40,
					Content = border,
				};
				badge.Show();
				badges.Add(badge);
			}
			return badges;
		}

		/// <summary>
		/// True when another display touches <paramref name="screen"/>'s left edge within
		/// <paramref name="stripWidthPx"/>. A sidebar sliding off to the left would then pass
		/// across that neighbour instead of leaving the screen, so it has to snap instead.
		/// </summary>
		public static bool HasLeftNeighbour(Screen screen, int stripWidthPx)
		{
			var strip = new System.Drawing.Rectangle(
				screen.Bounds.Left - stripWidthPx, screen.Bounds.Top, stripWidthPx, screen.Bounds.Height);
			return Screen.AllScreens.Any(s => s.DeviceName != screen.DeviceName && s.Bounds.IntersectsWith(strip));
		}
	}
}
