using Microsoft.Win32;
using System;

namespace StageManager.Services
{
	public static class Settings
	{
		private const string REG_KEY = @"SOFTWARE\StageManager\Settings";

		/// <summary>
		/// Stored value of <see cref="GetMonitorSelection"/> meaning "every display".
		/// Anything else is a display device name such as <c>\\.\DISPLAY2</c>.
		/// </summary>
		public const string AllMonitors = "*";

		public static void SetHideDesktopIcons(bool hideIcons)
		{
			using (var key = Registry.CurrentUser.CreateSubKey(REG_KEY))
				key?.SetValue("HideDesktopIcons", hideIcons ? 1 : 0, RegistryValueKind.DWord);
			Changed?.Invoke("HideDesktopIcons");
		}

		public static bool GetHideDesktopIcons()
		{
			using var key = Registry.CurrentUser.OpenSubKey(REG_KEY);
			var raw = key?.GetValue("HideDesktopIcons");
			if (raw is int i) return i != 0;
			// Legacy: old versions stored bool as REG_SZ — reader always fell through to default true.
			// Treat legacy strings the same way so existing users keep their expected behavior.
			return true;
		}

		/// <summary>
		/// The display selection of the last start: <see cref="AllMonitors"/> or one device name.
		/// Always written, so it can preselect the picker and serve the starts that skip it.
		/// </summary>
		public static string GetMonitorSelection()
		{
			using var key = Registry.CurrentUser.OpenSubKey(REG_KEY);
			return key?.GetValue("MonitorSelection") as string ?? AllMonitors;
		}

		public static void SetMonitorSelection(string selection)
		{
			using var key = Registry.CurrentUser.CreateSubKey(REG_KEY);
			key?.SetValue("MonitorSelection", selection, RegistryValueKind.String);
		}

		/// <summary>
		/// Whether the display picker opens on startup when more than one display is attached.
		/// </summary>
		public static bool GetAskMonitorOnStartup()
		{
			using var key = Registry.CurrentUser.OpenSubKey(REG_KEY);
			return key?.GetValue("AskMonitorOnStartup") is not int i || i != 0;
		}

		public static void SetAskMonitorOnStartup(bool ask)
		{
			using var key = Registry.CurrentUser.CreateSubKey(REG_KEY);
			key?.SetValue("AskMonitorOnStartup", ask ? 1 : 0, RegistryValueKind.DWord);
		}

		/// <summary>
		/// Raised after any setter below writes, with the value's name. Open sidebars listen so
		/// a change in the settings window takes effect without a restart.
		/// </summary>
		public static event Action<string>? Changed;

		/// <summary>Whether the stage runs. Off leaves only the tray icon — like turning Stage Manager off on macOS.</summary>
		public static bool GetStageEnabled() => GetBool("StageEnabled", true);
		public static void SetStageEnabled(bool value) => SetBool("StageEnabled", value);

		/// <summary>macOS "Show recent apps in Stage Manager". Off keeps the strip hidden until the pointer reaches the edge.</summary>
		public static bool GetShowRecentApps() => GetBool("ShowRecentApps", true);
		public static void SetShowRecentApps(bool value) => SetBool("ShowRecentApps", value);

		/// <summary>
		/// macOS "Show windows from an application": false = All at Once (every window of an app
		/// comes on stage together), true = One at a Time (only the window picked; the app's other
		/// windows wait in the strip). Changing it rebuilds the running stage.
		/// </summary>
		public static bool GetShowWindowsOneAtATime() => GetBool("ShowWindowsOneAtATime", false);
		public static void SetShowWindowsOneAtATime(bool value) => SetBool("ShowWindowsOneAtATime", value);

		/// <summary>macOS "Click wallpaper to reveal desktop".</summary>
		public static bool GetClickWallpaperRevealsDesktop() => GetBool("ClickWallpaperRevealsDesktop", true);
		public static void SetClickWallpaperRevealsDesktop(bool value) => SetBool("ClickWallpaperRevealsDesktop", value);

		public const int MinRecentApps = 1;
		public const int MaxRecentApps = 10;

		/// <summary>How many scenes the strip shows at once.</summary>
		public static int GetRecentAppsCount()
		{
			using var key = Registry.CurrentUser.OpenSubKey(REG_KEY);
			var value = key?.GetValue("RecentAppsCount") is int i ? i : 5;
			return Math.Clamp(value, MinRecentApps, MaxRecentApps);
		}

		public static void SetRecentAppsCount(int value)
		{
			using (var key = Registry.CurrentUser.CreateSubKey(REG_KEY))
				key?.SetValue("RecentAppsCount", Math.Clamp(value, MinRecentApps, MaxRecentApps), RegistryValueKind.DWord);
			Changed?.Invoke("RecentAppsCount");
		}

		/// <summary>Global shortcut that turns the stage on and off. Null when the user cleared it.</summary>
		public static HotkeyGesture? GetToggleHotkey()
		{
			using var key = Registry.CurrentUser.OpenSubKey(REG_KEY);
			var raw = key?.GetValue("ToggleHotkey") as string;
			if (raw is null)
				return HotkeyGesture.Default;
			return HotkeyGesture.TryParse(raw, out var gesture) ? gesture : null;
		}

		public static void SetToggleHotkey(HotkeyGesture? gesture)
		{
			using (var key = Registry.CurrentUser.CreateSubKey(REG_KEY))
				key?.SetValue("ToggleHotkey", gesture?.ToString() ?? "", RegistryValueKind.String);
			Changed?.Invoke("ToggleHotkey");
		}

		private static bool GetBool(string name, bool fallback)
		{
			using var key = Registry.CurrentUser.OpenSubKey(REG_KEY);
			return key?.GetValue(name) is int i ? i != 0 : fallback;
		}

		private static void SetBool(string name, bool value)
		{
			using (var key = Registry.CurrentUser.CreateSubKey(REG_KEY))
				key?.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
			Changed?.Invoke(name);
		}
	}
}
