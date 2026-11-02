using StageManager.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace StageManager
{
	/// <summary>
	/// Everything adjustable, modelled on macOS Desktop &amp; Dock → Stage Manager. Every control
	/// writes straight through <see cref="Settings"/>; open sidebars pick changes up from
	/// <see cref="Settings.Changed"/>, so there is no Apply button.
	/// </summary>
	public partial class SettingsWindow : Window
	{
		private const string AutoStartName = "StageManager";

		private readonly StageHost _host;
		private bool _loading;
		private bool _recording;
		private List<Window>? _badges;

		internal SettingsWindow(StageHost host)
		{
			_host = host;
			InitializeComponent();

			_loading = true;
			recentAppsSlider.Minimum = Settings.MinRecentApps;
			recentAppsSlider.Maximum = Settings.MaxRecentApps;
			recentAppsSlider.Value = Settings.GetRecentAppsCount();
			recentAppsValue.Text = Settings.GetRecentAppsCount().ToString();
			showRecentAppsSwitch.IsChecked = Settings.GetShowRecentApps();
			var oneAtATime = Settings.GetShowWindowsOneAtATime();
			allAtOnceChoice.IsChecked = !oneAtATime;
			oneAtATimeChoice.IsChecked = oneAtATime;
			ShowWindowsModeHint(oneAtATime);
			clickWallpaperSwitch.IsChecked = Settings.GetClickWallpaperRevealsDesktop();
			hideIconsSwitch.IsChecked = Settings.GetHideDesktopIcons();
			askOnStartupSwitch.IsChecked = Settings.GetAskMonitorOnStartup();
			autostartSwitch.IsChecked = AutoStart.IsStartup(AutoStartName);
			BuildDisplayChoices();
			_loading = false;

			RefreshState();
			_host.StateChanged += RefreshState;
			Closed += (_, _) =>
			{
				_host.StateChanged -= RefreshState;
				if (_recording) EndRecording();
				CloseBadges();
			};
		}

		/// <summary>Re-reads what can change behind the window's back (the shortcut toggles the stage).</summary>
		private void RefreshState()
		{
			enabledSwitch.IsChecked = _host.IsEnabled;
			if (!_recording)
				ShowShortcut();
		}

		private void ShowShortcut()
		{
			var gesture = Settings.GetToggleHotkey();
			shortcutButton.Content = gesture?.DisplayText ?? "Record shortcut";
			clearShortcutButton.IsEnabled = gesture is not null;

			if (gesture is not null && _host.ActiveHotkey is null)
				shortcutHint.Text = "Another app already uses this shortcut — record a different one.";
			else
				shortcutHint.Text = "Turns Stage Manager on and off from anywhere.";
		}

		private async void EnabledSwitch_Click(object sender, RoutedEventArgs e)
		{
			enabledSwitch.IsEnabled = false;
			try { await _host.SetEnabledAsync(enabledSwitch.IsChecked == true); }
			finally
			{
				enabledSwitch.IsEnabled = true;
				RefreshState();
			}
		}

		#region Shortcut recording

		private void ShortcutButton_Click(object sender, RoutedEventArgs e)
		{
			if (_recording) return;
			_recording = true;
			_host.SuspendHotkey(true);
			shortcutButton.Content = "Press a shortcut…";
			shortcutHint.Text = "Hold Ctrl, Alt, Shift or Win and press a key. Esc cancels.";
			shortcutButton.Focus();
		}

		private void ShortcutButton_PreviewKeyDown(object sender, KeyEventArgs e)
		{
			if (!_recording) return;
			e.Handled = true;

			// Alt turns every key into Key.System; the real key is in SystemKey.
			var key = e.Key == Key.System ? e.SystemKey : e.Key;
			var modifiers = Keyboard.Modifiers;

			if (key == Key.Escape && modifiers == ModifierKeys.None)
			{
				EndRecording();
				return;
			}

			if (HotkeyGesture.IsModifierKey(key))
			{
				// Echo the held modifiers so the user sees the recorder react.
				var held = new HotkeyGesture(modifiers, Key.None).ToString();
				shortcutButton.Content = held.Replace("None", "").Replace("+", " + ").TrimEnd(' ', '+') + " + …";
				return;
			}

			var gesture = new HotkeyGesture(modifiers, key);
			if (!gesture.IsValid)
			{
				shortcutHint.Text = "A shortcut needs at least one of Ctrl, Alt, Shift or Win.";
				return;
			}

			Settings.SetToggleHotkey(gesture);
			EndRecording();
		}

		private void ShortcutButton_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
		{
			if (_recording) EndRecording();
		}

		private void EndRecording()
		{
			_recording = false;
			_host.SuspendHotkey(false);
			ShowShortcut();
		}

		private void ClearShortcut_Click(object sender, RoutedEventArgs e)
		{
			Settings.SetToggleHotkey(null);
			ShowShortcut();
		}

		#endregion

		private void ShowRecentApps_Click(object sender, RoutedEventArgs e) =>
			Settings.SetShowRecentApps(showRecentAppsSwitch.IsChecked == true);

		private void RecentAppsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
		{
			var count = (int)Math.Round(e.NewValue);
			if (recentAppsValue is not null)
				recentAppsValue.Text = count.ToString();
			if (!_loading && count != Settings.GetRecentAppsCount())
				Settings.SetRecentAppsCount(count);
		}

		private void WindowsMode_Checked(object sender, RoutedEventArgs e)
		{
			var oneAtATime = ReferenceEquals(sender, oneAtATimeChoice);
			ShowWindowsModeHint(oneAtATime);
			if (_loading || oneAtATime == Settings.GetShowWindowsOneAtATime()) return;
			// The host regroups a running stage around the new mode.
			Settings.SetShowWindowsOneAtATime(oneAtATime);
		}

		private void ShowWindowsModeHint(bool oneAtATime)
		{
			// Checked fires during InitializeComponent for a RadioButton with IsChecked in XAML;
			// the hint may not exist yet then.
			if (windowsModeHint is null) return;
			windowsModeHint.Text = oneAtATime
				? "Only the window you pick comes on stage; the app's other windows wait in the strip."
				: "Switching to an app brings all of its windows on stage together.";
		}

		private void ClickWallpaper_Click(object sender, RoutedEventArgs e) =>
			Settings.SetClickWallpaperRevealsDesktop(clickWallpaperSwitch.IsChecked == true);

		private void HideIcons_Click(object sender, RoutedEventArgs e) =>
			Settings.SetHideDesktopIcons(hideIconsSwitch.IsChecked == true);

		private void AskOnStartup_Click(object sender, RoutedEventArgs e) =>
			Settings.SetAskMonitorOnStartup(askOnStartupSwitch.IsChecked == true);

		private void Autostart_Click(object sender, RoutedEventArgs e) =>
			AutoStart.SetStartup(AutoStartName, autostartSwitch.IsChecked == true);

		#region Displays

		private void BuildDisplayChoices()
		{
			var screens = MonitorLayout.Ordered();
			var current = Settings.GetMonitorSelection();
			if (current != Settings.AllMonitors && MonitorLayout.Find(current) is null)
				current = Settings.AllMonitors;

			if (screens.Length <= 1)
			{
				displayChoices.Children.Add(new TextBlock
				{
					Style = (Style)FindResource("RowHint"),
					Margin = new Thickness(0, 0, 0, 6),
					Text = "Only one display is connected. Stage Manager runs on it.",
				});
				askOnStartupSwitch.IsEnabled = false;
				return;
			}

			AddChoice(Settings.AllMonitors, "All displays", $"A separate stage on each of the {screens.Length} displays", current);
			foreach (var screen in screens)
				AddChoice(screen.DeviceName, $"Only display {MonitorLayout.Number(screen)}",
					MonitorLayout.Describe(screen) + " — other displays are left alone", current);
		}

		private void AddChoice(string selection, string title, string detail, string current)
		{
			var content = new StackPanel();
			content.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("RowLabel") });
			content.Children.Add(new TextBlock { Text = detail, Style = (Style)FindResource("RowHint"), Margin = new Thickness(0) });

			var choice = new RadioButton
			{
				GroupName = "displays",
				Style = (Style)FindResource("ChoiceRow"),
				Content = content,
				Tag = selection,
				IsChecked = string.Equals(selection, current, StringComparison.OrdinalIgnoreCase),
			};
			choice.Checked += DisplayChoice_Checked;
			displayChoices.Children.Add(choice);
		}

		private async void DisplayChoice_Checked(object sender, RoutedEventArgs e)
		{
			if (_loading || sender is not RadioButton { Tag: string selection }) return;

			displayChoices.IsEnabled = false;
			try { await _host.ApplyMonitorSelectionAsync(selection); }
			finally { displayChoices.IsEnabled = true; }
		}

		private async void Identify_Click(object sender, RoutedEventArgs e)
		{
			CloseBadges();
			var badges = MonitorLayout.ShowNumberBadges();
			_badges = badges;
			await Task.Delay(TimeSpan.FromSeconds(2.5));
			if (ReferenceEquals(_badges, badges))
				CloseBadges();
		}

		private void CloseBadges()
		{
			if (_badges is null) return;
			foreach (var badge in _badges)
				badge.Close();
			_badges = null;
		}

		#endregion
	}
}
