using AsyncAwaitBestPractices;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using StageManager.Services;

namespace StageManager
{
	public partial class App : Application
	{
		internal static string? RestoreScenesPath { get; private set; }

		/// <summary>
		/// The layout the previous run saved when it quit, when there is one. Kept apart from
		/// <see cref="RestoreScenesPath"/>: that one also means "restarted by an update", which
		/// skips the display picker and cleans the staging folder.
		/// </summary>
		internal static string? SessionScenesPath { get; private set; }

		/// <summary>
		/// The snapshot belongs to the first stage after a start only. A stage rebuilt later
		/// (turned off and on) must group windows afresh, not replay the old layout.
		/// </summary>
		internal static void ForgetRestoreScenes()
		{
			RestoreScenesPath = null;
			SessionScenesPath = null;
		}

		protected override void OnStartup(StartupEventArgs e)
		{
			base.OnStartup(e);

			// Before anything that logs a frame number, so the count covers the whole run.
			FrameClock.Start();

			RestoreScenesPath = ParseRestoreScenesArg(e.Args);
			if (RestoreScenesPath is null && File.Exists(SceneSnapshot.SessionPath))
				SessionScenesPath = SceneSnapshot.SessionPath;
			UpdateService.CleanupOldVersion();
			if (RestoreScenesPath is null)
				UpdateService.CleanupStagingFolder();

			Services.ThemeManager.ApplyTheme();
			Services.ThemeManager.StartListening();

			// Consent is per process and only affects sessions started after it lands, so
			// it goes out as early as possible. Not awaited: the tray tiles it misses are
			// parked off-screen, where the indicator it suppresses cannot be seen anyway.
			Composition.CaptureBorder.RequestAsync().SafeFireAndForget();

			// Log-only — intentionally NOT setting args.Handled so the app terminates
			DispatcherUnhandledException += (s, args) =>
			{
				Log.Fatal("CRASH", $"UI thread: {args.Exception}");
			};

			AppDomain.CurrentDomain.UnhandledException += (s, args) =>
			{
				Log.Fatal("CRASH", $"Unhandled: {args.ExceptionObject}");
			};

			TaskScheduler.UnobservedTaskException += (s, args) =>
			{
				Log.Fatal("CRASH", $"Unobserved task: {args.Exception}");
			};

			if (!RunDisplayPicker())
			{
				Log.Info("STARTUP", "Display picker cancelled, exiting");
				Shutdown();
				return;
			}

			Host = new StageHost();
			Host.Initialize(monitors: null);
		}

		internal static StageHost? Host { get; private set; }

		/// <summary>
		/// Shows the display picker when it applies and saves the choice. False when the user
		/// quit from it.
		/// <para>
		/// The picker only opens with more than one display attached, while the stage is on and
		/// the user has not opted out. A restart after an update never shows it: that start
		/// restores the running layout, so it reuses the selection it was running with.
		/// </para>
		/// </summary>
		private static bool RunDisplayPicker()
		{
			if (MonitorLayout.Ordered().Length <= 1 || RestoreScenesPath is not null
				|| !Settings.GetStageEnabled() || !Settings.GetAskMonitorOnStartup())
				return true;

			var picker = new MonitorPickerWindow(Settings.GetMonitorSelection());
			if (picker.ShowDialog() != true)
				return false;

			Settings.SetMonitorSelection(picker.Selection);
			if (picker.Remember)
				Settings.SetAskMonitorOnStartup(false);
			return true;
		}

		// Signing out or shutting Windows down ends the process without the tray's Quit.
		protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
		{
			Host?.SaveSession();
			base.OnSessionEnding(e);
		}

		protected override void OnExit(ExitEventArgs e)
		{
			Services.ThemeManager.StopListening();
			base.OnExit(e);
		}

		private static string? ParseRestoreScenesArg(string[] args)
		{
			var index = Array.IndexOf(args, "--restore-scenes");
			if (index < 0 || index + 1 >= args.Length)
				return null;

			var path = Path.GetFullPath(args[index + 1]);
			var expectedDir = Path.GetFullPath(UpdateService.StagingFolder);

			if (!path.StartsWith(expectedDir, StringComparison.OrdinalIgnoreCase))
				return null;

			return path;
		}
	}
}
