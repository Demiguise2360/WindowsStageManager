using AsyncAwaitBestPractices;
using StageManager.Controls;
using StageManager.Native;
using StageManager.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Screen = System.Windows.Forms.Screen;

namespace StageManager
{
	/// <summary>
	/// Owns everything that outlives a single sidebar: the tray menu, the toggle shortcut, the
	/// settings window, the app lifetime, and — while the stage is on — the one
	/// <see cref="Native.WindowsManager"/> feeding every per-display SceneManager.
	/// <para>
	/// One <see cref="MainWindow"/> runs per managed display, each with its own SceneManager
	/// restricted to that display. Turning the stage off closes them all, which puts every
	/// window back on screen; turning it on builds a fresh set.
	/// </para>
	/// </summary>
	internal sealed class StageHost
	{
		// How long a sidebar takes to slide off before the stage is torn down. Matches the
		// stow animation in MainWindow.ApplyWindowMode.
		private static readonly TimeSpan StowDuration = TimeSpan.FromMilliseconds(520);

		private readonly List<MainWindow> _sidebars = new();
		private TrayController? _tray;
		private GlobalHotkey? _hotkey;
		private SettingsWindow? _settingsWindow;
		private bool _quitting;
		// Set while the stage is being built or torn down; a toggle arriving meanwhile is dropped.
		private bool _busy;

		public WindowsManager WindowsManager { get; private set; } = new();

		public IReadOnlyList<MainWindow> Sidebars => _sidebars;

		public bool IsEnabled => _sidebars.Count > 0;

		/// <summary>The toggle shortcut as registered, or null when unset or taken by another app.</summary>
		public HotkeyGesture? ActiveHotkey => _hotkey is { IsRegistered: true } h ? h.Gesture : null;

		public event Action? StateChanged;

		/// <summary>
		/// Builds the tray and shortcut, then the stage when it is enabled.
		/// <paramref name="monitors"/> is what the startup picker chose; null means use the saved
		/// selection.
		/// </summary>
		public void Initialize(IReadOnlyList<Screen>? monitors)
		{
			_tray = new TrayController(this);
			RegisterHotkey();
			Settings.Changed += OnSettingChanged;

			_displaySignature = DisplaySignature();
			SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
			SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

			if (Settings.GetStageEnabled())
				StartStage(monitors ?? ResolveSavedMonitors());

			_tray.Refresh();
		}

		#region Display changes

		// Everything a sidebar is laid out from: which displays exist, where, and their work
		// areas (the taskbar moving or auto-hiding changes those without any display event).
		private string _displaySignature = "";
		private DispatcherTimer? _displaySettle;

		// Long enough to ride out a display blinking off and on (input switching, a DisplayPort
		// monitor waking) — a rebuild for each blink would shuffle every window twice.
		private static readonly TimeSpan DisplaySettleDelay = TimeSpan.FromMilliseconds(2500);

		private static string DisplaySignature() => string.Join("|", Screen.AllScreens
			.OrderBy(s => s.DeviceName, StringComparer.OrdinalIgnoreCase)
			.Select(s => $"{s.DeviceName}={s.Bounds}/{s.WorkingArea}{(s.Primary ? "*" : "")}"));

		// SystemEvents raises on its own thread.
		private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
			Application.Current?.Dispatcher.BeginInvoke(ScheduleDisplayCheck);

		private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
		{
			if (e.Category == UserPreferenceCategory.Desktop)
				Application.Current?.Dispatcher.BeginInvoke(ScheduleDisplayCheck);
		}

		private void ScheduleDisplayCheck()
		{
			if (_quitting) return;
			if (_displaySettle is null)
			{
				_displaySettle = new DispatcherTimer { Interval = DisplaySettleDelay };
				_displaySettle.Tick += (_, _) =>
				{
					_displaySettle.Stop();
					CheckDisplays();
				};
			}
			// Restart: settle on the LAST change of a burst.
			_displaySettle.Stop();
			_displaySettle.Start();
		}

		/// <summary>
		/// Rebuilds the stage when the displays or their work areas changed. Every sidebar is laid
		/// out once from its display's geometry, a sidebar may belong to a display that is gone,
		/// and a new display can sit where the old park point was — only a rebuild settles all
		/// three. The scene layout is carried across, so groupings survive.
		/// </summary>
		private void CheckDisplays()
		{
			var signature = DisplaySignature();
			if (signature == _displaySignature) return;
			Log.Info("DISPLAY", $"Displays changed: {_displaySignature} → {signature}");
			_displaySignature = signature;

			if (IsEnabled)
				RebuildStageAsync("displays changed", keepLayout: true).SafeFireAndForget();
		}

		#endregion

		private bool _rebuildQueued;

		/// <summary>
		/// Turns the stage off and on again around a change that alters its foundations — the
		/// displays, or what a scene is (One at a Time). With <paramref name="keepLayout"/> the
		/// scene groupings are handed across in memory; without it windows are grouped afresh.
		/// </summary>
		private async Task RebuildStageAsync(string reason, bool keepLayout)
		{
			if (_rebuildQueued) return;
			_rebuildQueued = true;
			try
			{
				// A toggle or another rebuild in flight finishes first.
				while (_busy)
					await Task.Delay(100);
			}
			finally
			{
				_rebuildQueued = false;
			}

			if (!IsEnabled || _quitting) return;

			Log.Action($"Rebuilding stage: {reason}");
			var layout = keepLayout ? CreateSnapshot() : null;
			await StopStageAsync();
			SceneSnapshot.CarryOver = layout;
			StartStage(ResolveSavedMonitors());
		}

		/// <summary>The saved display selection, or every display when it names one that is gone.</summary>
		public static IReadOnlyList<Screen> ResolveSavedMonitors()
		{
			var saved = Settings.GetMonitorSelection();
			if (saved != Settings.AllMonitors && MonitorLayout.Find(saved) is { } single)
				return new[] { single };
			return MonitorLayout.Ordered();
		}

		public void Toggle() => SetEnabledAsync(!IsEnabled).SafeFireAndForget();

		public async Task SetEnabledAsync(bool enabled)
		{
			if (_busy || _quitting || enabled == IsEnabled)
				return;

			Log.Action($"Stage Manager {(enabled ? "ON" : "OFF")}");
			Settings.SetStageEnabled(enabled);

			if (enabled)
				StartStage(ResolveSavedMonitors());
			else
				await StopStageAsync();
		}

		/// <summary>
		/// Switches to a new display selection. A running stage is rebuilt around it; windows
		/// come back on screen in between, exactly as when the stage is turned off and on.
		/// </summary>
		public async Task ApplyMonitorSelectionAsync(string selection)
		{
			if (selection == Settings.GetMonitorSelection())
				return;

			Settings.SetMonitorSelection(selection);
			if (!IsEnabled || _busy)
				return;

			await StopStageAsync();
			StartStage(ResolveSavedMonitors());
		}

		private void StartStage(IReadOnlyList<Screen> monitors)
		{
			if (IsEnabled || monitors.Count == 0) return;
			_busy = true;
			try
			{
				// A previous run of the stage shut the capture layer down for good; this one needs it.
				CompositionThumbnail.ResetAfterShutdown();
				// Null = every display, which is also what each sidebar assumes when only one exists.
				var managed = monitors.Count >= Screen.AllScreens.Length ? null : monitors.Select(m => m.DeviceName);
				WindowsManager = new WindowsManager(managed);

				// Primary first so it is the main window when it is part of the selection.
				var ordered = monitors.OrderByDescending(m => m.Primary).ToArray();
				foreach (var monitor in ordered)
				{
					var sidebar = new MainWindow(this, monitor);
					_sidebars.Add(sidebar);
					sidebar.Show();
				}

				// WPF makes the first window ever created the main window — the display picker,
				// when it was shown. Point it at a live sidebar instead.
				Application.Current.MainWindow = _sidebars[0];

				Log.Info("STARTUP", $"StageHost: {_sidebars.Count} sidebar(s) on {string.Join(", ", ordered.Select(m => m.DeviceName))}");
			}
			finally
			{
				_busy = false;
				OnStateChanged();
			}
		}

		/// <summary>
		/// Slides every sidebar off, then closes them. Closing stops each SceneManager, which puts
		/// that display's windows back where they were — none are minimized, the way macOS leaves
		/// every window in place when Stage Manager goes off.
		/// </summary>
		private async Task StopStageAsync()
		{
			if (!IsEnabled) return;
			_busy = true;
			App.ForgetRestoreScenes();
			SceneSnapshot.CarryOver = null;
			try
			{
				var sidebars = _sidebars.ToArray();
				foreach (var sidebar in sidebars)
					sidebar.BeginStow();

				await Task.Delay(StowDuration);

				foreach (var sidebar in sidebars)
					sidebar.CloseAndRestoreWindows(minimizeHidden: false);
			}
			finally
			{
				_busy = false;
				OnStateChanged();
			}
		}

		/// <summary>
		/// Closes every sidebar, then ends the process. Each close stops its SceneManager, which
		/// puts that display's windows back; the shared WindowsManager unhooks with the last.
		/// </summary>
		public void Quit()
		{
			if (_quitting) return;
			_quitting = true;

			_settingsWindow?.Close();

			// Before the sidebars close: closing stops each SceneManager and empties its scenes.
			SaveSession();

			foreach (var sidebar in _sidebars.ToArray())
				sidebar.CloseAndRestoreWindows(minimizeHidden: true);

			Settings.Changed -= OnSettingChanged;
			SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
			SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
			_displaySettle?.Stop();
			_hotkey?.Dispose();
			_tray?.Dispose();
			Application.Current.Shutdown();
		}

		internal void OnSidebarClosed(MainWindow sidebar)
		{
			_sidebars.Remove(sidebar);
		}

		public void ShowSettings()
		{
			_tray?.CloseMenu();

			if (_settingsWindow is not null)
			{
				_settingsWindow.Activate();
				return;
			}

			_settingsWindow = new SettingsWindow(this);
			_settingsWindow.Closed += (_, _) => _settingsWindow = null;
			_settingsWindow.Show();
			_settingsWindow.Activate();
		}

		/// <summary>
		/// Keeps the current layout for the next start. With the stage off there is no layout,
		/// and the one saved while it last ran is kept rather than overwritten with nothing.
		/// </summary>
		public void SaveSession()
		{
			if (!_sidebars.Any(s => s.SceneManager is not null))
				return;
			SceneSnapshot.SaveSession(CreateSnapshot());
		}

		public SceneSnapshot.Snapshot CreateSnapshot() =>
			SceneSnapshot.Combine(_sidebars.Where(s => s.SceneManager is not null).Select(s => s.SceneManager.CreateSnapshot()));

		/// <summary>
		/// Releases the toggle shortcut while the settings window records a new one — pressing
		/// the current combination there must not switch the stage.
		/// </summary>
		public void SuspendHotkey(bool suspend)
		{
			if (suspend)
			{
				_hotkey?.Dispose();
				_hotkey = null;
			}
			else
			{
				RegisterHotkey();
			}
		}

		private void RegisterHotkey()
		{
			_hotkey?.Dispose();
			_hotkey = null;

			if (Settings.GetToggleHotkey() is { } gesture)
			{
				_hotkey = new GlobalHotkey(gesture);
				_hotkey.Pressed += Toggle;
			}
		}

		private void OnSettingChanged(string name)
		{
			if (name == "ToggleHotkey")
				RegisterHotkey();

			// Every scene key changes meaning, so the stage is regrouped from scratch.
			if (name == "ShowWindowsOneAtATime" && IsEnabled)
				RebuildStageAsync("show windows one at a time changed", keepLayout: false).SafeFireAndForget();

			OnStateChanged();
		}

		private void OnStateChanged()
		{
			_tray?.Refresh();
			StateChanged?.Invoke();
		}
	}
}
