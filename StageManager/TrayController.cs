using Hardcodet.Wpf.TaskbarNotification;
using StageManager.Services;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace StageManager
{
	/// <summary>
	/// The tray icon and its menu. Lives as long as the process, independent of the sidebars, so
	/// the menu (and the way back on) survives turning the stage off.
	/// </summary>
	internal sealed class TrayController : IDisposable
	{
		private readonly StageHost _host;
		private readonly TaskbarIcon _icon;
		private readonly MenuItem _toggleItem;
		private readonly TextBlock _toggleShortcut;
		private readonly MenuItem _updateItem;
		private readonly UpdateService _updateService = new();

		/// <summary>
		/// True while the menu is open. The sidebars' global mouse hook stands down meanwhile, so
		/// clicks on the menu are not read as clicks on the desktop or a tile.
		/// </summary>
		public static volatile bool IsMenuOpen;

		public TrayController(StageHost host)
		{
			_host = host;

			var menu = new ContextMenu { Style = Style("TrayContextMenuStyle") };
			menu.Opened += (_, _) => { IsMenuOpen = true; Refresh(); };
			menu.Closed += (_, _) => IsMenuOpen = false;

			menu.Items.Add(new MenuItem
			{
				Style = Style("TrayMenuLabelStyle"),
				Header = $"Stage Manager v{UpdateService.GetCurrentVersion()}",
			});
			menu.Items.Add(Separator());

			// Header = name on the left, shortcut greyed on the right, like a macOS menu item.
			_toggleShortcut = new TextBlock { Margin = new Thickness(24, 0, 0, 0) };
			_toggleShortcut.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
			var toggleHeader = new DockPanel { LastChildFill = true };
			DockPanel.SetDock(_toggleShortcut, Dock.Right);
			toggleHeader.Children.Add(_toggleShortcut);
			toggleHeader.Children.Add(new TextBlock { Text = "Stage Manager" });
			_toggleItem = Item(toggleHeader, () => _host.Toggle());
			_toggleItem.IsCheckable = true;
			menu.Items.Add(_toggleItem);

			menu.Items.Add(Item("Settings…", () => _host.ShowSettings()));
			menu.Items.Add(Separator());
			menu.Items.Add(Item("Project page", () => Process.Start(new ProcessStartInfo("https://github.com/awaescher/StageManager") { UseShellExecute = true })));

			_updateItem = Item("Check for updates", () => OnUpdateClicked());
			_updateItem.StaysOpenOnClick = true;
			menu.Items.Add(_updateItem);

			menu.Items.Add(Separator());
			menu.Items.Add(Item("Quit", () => _host.Quit()));

			_icon = new TaskbarIcon
			{
				IconSource = new BitmapImage(new Uri("pack://application:,,,/StageManager.ico")),
				MenuActivation = PopupActivationMode.LeftOrRightClick,
				NoLeftClickDelay = true,
				ContextMenu = menu,
			};

			Refresh();
		}

		/// <summary>Re-reads the stage state and shortcut into the menu and tooltip.</summary>
		public void Refresh()
		{
			// IsCheckable toggles IsChecked itself on click; the host is the source of truth.
			_toggleItem.IsChecked = _host.IsEnabled;
			_toggleShortcut.Text = Settings.GetToggleHotkey()?.DisplayText ?? "";
			_icon.ToolTipText = _host.IsEnabled ? "Stage Manager" : "Stage Manager (off)";
		}

		public void CloseMenu()
		{
			if (_icon.ContextMenu is { IsOpen: true } menu)
				menu.IsOpen = false;
		}

		public void Dispose()
		{
			_icon.Dispose();
			_updateService.Dispose();
		}

		private static Style Style(string key) => (Style)Application.Current.FindResource(key);

		private static Separator Separator() => new() { Style = Style("TrayMenuSeparatorStyle") };

		private MenuItem Item(object header, Action onClick)
		{
			var item = new MenuItem { Style = Style("TrayMenuItemStyle"), Header = header };
			item.Click += (_, _) => onClick();
			return item;
		}

		#region Updates

		private enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Error }

		private UpdateState _updateState = UpdateState.Idle;
		private UpdateInfo? _availableUpdate;
		private double _downloadProgress;
		private string? _downloadedPath;

		private string UpdateMenuText => _updateState switch
		{
			UpdateState.Idle => "Check for updates",
			UpdateState.Checking => "Checking...",
			UpdateState.UpToDate => "Up to date",
			UpdateState.Available => $"Update to {_availableUpdate!.TagName}",
			UpdateState.Downloading => $"Downloading...  {_downloadProgress:P0}",
			UpdateState.Ready => "Restart to update",
			UpdateState.Error => "Update failed · Retry",
			_ => "Check for updates"
		};

		private void SetUpdateState(UpdateState state)
		{
			_updateState = state;
			_updateItem.Header = UpdateMenuText;
		}

		private async void OnUpdateClicked()
		{
			switch (_updateState)
			{
				case UpdateState.Idle:
				case UpdateState.UpToDate:
				case UpdateState.Error:
					await PerformUpdateCheckAsync();
					break;
				case UpdateState.Available:
					await PerformDownloadAsync();
					break;
				case UpdateState.Ready:
					PerformApplyAndRestart();
					break;
			}
		}

		private async Task PerformUpdateCheckAsync()
		{
			SetUpdateState(UpdateState.Checking);
			try
			{
				var update = await _updateService.CheckForUpdateAsync();
				if (update is null)
				{
					SetUpdateState(UpdateState.UpToDate);
					_ = Task.Delay(3000).ContinueWith(_ =>
					{
						if (_updateState == UpdateState.UpToDate)
							SetUpdateState(UpdateState.Idle);
					}, TaskScheduler.FromCurrentSynchronizationContext());
				}
				else
				{
					_availableUpdate = update;
					SetUpdateState(UpdateState.Available);
				}
			}
			catch (Exception ex)
			{
				Log.Fatal("UPDATE", $"Update check failed: {ex}");
				SetUpdateState(UpdateState.Error);
			}
		}

		private async Task PerformDownloadAsync()
		{
			if (_availableUpdate is null) return;
			SetUpdateState(UpdateState.Downloading);
			try
			{
				var progress = new Progress<double>(p =>
				{
					_downloadProgress = p;
					_updateItem.Header = UpdateMenuText;
				});

				_downloadedPath = await _updateService.DownloadUpdateAsync(_availableUpdate, progress);
				SetUpdateState(UpdateState.Ready);
			}
			catch (Exception ex)
			{
				Log.Fatal("UPDATE", $"Update download failed: {ex}");
				SetUpdateState(UpdateState.Error);
			}
		}

		private void PerformApplyAndRestart()
		{
			if (_downloadedPath is null) return;
			try
			{
				CloseMenu();
				var snapshotPath = SceneSnapshot.Save(_host.CreateSnapshot());
				UpdateService.ApplyUpdate(_downloadedPath);
				UpdateService.LaunchAndExit(snapshotPath);
			}
			catch (Exception ex)
			{
				Log.Fatal("UPDATE", $"Update apply failed: {ex}");
				SetUpdateState(UpdateState.Error);
			}
		}

		#endregion
	}
}
