using AsyncAwaitBestPractices;
using Microsoft.Xaml.Behaviors.Core;
using SharpHook;
using StageManager.Animations;
using StageManager.Controls;
using StageManager.Model;
using StageManager.Native;
using StageManager.Services;
using StageManager.Native.PInvoke;
using StageManager.Native.Interop;
using StageManager.Native.Window;
using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace StageManager
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window, INotifyPropertyChanged
	{
		private const int TIMERINTERVAL_MILLISECONDS = 500;
		// How many scenes the strip shows (Settings → Recent apps). Cached: read on every sync.
		private int _maxScenes = Settings.GetRecentAppsCount();
		// Settings → "Show recent apps". Off keeps the strip stowed until the pointer reaches the edge.
		private bool _showRecentApps = Settings.GetShowRecentApps();
		// Resting look of the tray, measured off macOS 26.5.2 to within
		// 0.2 pt RMS over 16 corners: every horizontal card edge tilts by
		// atan((yEdge - screenCenterY) / d), positive = right end rises, where
		// d = 1379 pt on a 1169 pt screen. Position drives the shape — angles do
		// NOT depend on row count or index. d scales with monitor height so the
		// look is resolution-independent; the pivot is the FULL monitor centre
		// (macOS pivots on screenHeight/2 including the menu bar), not the work
		// area centre. CompositionThumbnail turns each angle pair into the shear
		// + sprite rotation that reproduce exactly those two slopes.
		private const double EdgePerspectiveDistanceRatio = 1379.0 / 1169.0;
		// Fraction of sidebar width at which a normal window's left edge triggers auto-stow.
		// Lower = boundary sits further left, so wider windows keep the tray visible (more window estate).
		private const double STOW_OVERLAP_FRACTION = 0.25;
		// Mirrors CompositionThumbnail CornerRadius="8" in MainWindow.xaml so the
		// borrowed live drag ghost keeps the same rounded corners as the tray tile.
		private const double SidebarThumbCornerRadius = 8.0;
		private IntPtr _thisHandle;

		// SharpHook runs one global hook per process, so every sidebar subscribes to the same one.
		private static TaskPoolGlobalHook? _hook;
		private static int _hookUsers;

		private readonly StageHost _host;
		// The display this sidebar and its SceneManager run the stage on.
		private readonly System.Windows.Forms.Screen _monitor;
		// Physical pixels (hook / SetWindowPos space) and DIPs (WPF space) of that display.
		private readonly System.Drawing.Rectangle _monitorPx;
		private readonly System.Drawing.Rectangle _workPx;
		private readonly Rect _workDip;
		// False when another display borders this one on the left: sliding off to the left would
		// carry the sidebar across that neighbour, so stowing snaps it away instead.
		private bool _canSlide = true;

		private double DockedLeft => _workDip.Left;
		private double DockedTop => _workDip.Top;
		private double ParkedLeft => _canSlide
			? DockedLeft - Width
			// Past the right edge of every display — nowhere to be seen, and DWM culls the tiles.
			: System.Windows.Forms.SystemInformation.VirtualScreen.Right / MonitorLayout.PixelsPerDip + 100;
		private WindowMode _mode;
		// One-shot: snap the next sidebar mode change instead of sliding. Set when a
		// window is dropped into the tray so the new tile appears in place, not via unstow.
		private bool _suppressNextModeSlide;
		private double _lastWidth;
		private Timer? _overlapCheckTimer;
		private long _mouseX;
		private long _mouseY;
		private CancellationTokenSource? _cancellationTokenSource;
		private SceneModel? _removedCurrentScene;
		// Guards SceneManager_CurrentSceneSelectionChanged against nested entry via
		// the synchronous CollectionChanged handlers it triggers.
		private bool _inSelectionChange;
		private SceneModel? _mouseDownScene;
		private bool _hideDesktopIcons;

		// WPF-native drag state (all UI thread, no cross-thread issues)
		private enum SidebarDragPhase { None, InSidebar, InBuffer, PastBuffer }
		private SceneModel? _wpfDragScene;
		private Point _wpfDragStartPoint;
		private SidebarDragPhase _sidebarDragPhase;
		private bool IsSidebarDragging => _sidebarDragPhase != SidebarDragPhase.None;
		private IWindow? _sidebarDragWindow;
		private Rect _sidebarDragThumbRect;
		private Rect _sidebarDragWindowRect;
		private Point _sidebarDragDpi;
		private double _sidebarDragBufferLeft;
		private double _sidebarDragBufferRight;
		private readonly SceneTransitionAnimator _sceneTransitionAnimator = new SceneTransitionAnimator();
		private readonly SidebarDragGhost _sidebarDragGhost;
		private readonly DebugZoneOverlay _debugZoneOverlay;

		private DragDropManager? _dragDropManager;
		private readonly IconOverlayManager _iconOverlay = new();
		private string? _filterProcessKey;

		// Filter morph: enter decelerates (EaseOut), exit accelerates (EaseIn).
		private static readonly Duration _filterMorphDuration = new Duration(TimeSpan.FromMilliseconds(250));
		private static readonly IEasingFunction _filterEaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
		private static readonly IEasingFunction _filterEaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };
		private readonly Dictionary<Guid, int> _filterAnimGen = new();
		private int _filterIconGen;

		// Grace window covering WindowsManager.MouseHook → DesktopShortClick latency (gated by
		// the OS double-click time, typically ~500ms). SharpHook clears the filter at T=0 but
		// the desktop toggle event fires later — predicate must report "filter was just cleared"
		// during the gap to suppress the toggle.
		private DateTime _filterClearedAt = DateTime.MinValue;
		private static readonly TimeSpan _filterClearGrace = TimeSpan.FromMilliseconds(750);

		// Flips true when the startup slide-in animation completes. Until then, OnRenderSizeChanged
		// must NOT yank Left back to 0 — the window is intentionally parked at -Width so DWM thumbnails
		// and scene previews stay culled while setup runs (scenes added, foreground scene switched).
		private bool _startupSlideComplete = false;

		public event PropertyChangedEventHandler? PropertyChanged;

		public bool EnableWindowPullToScene = true;

		internal MainWindow(StageHost host, System.Windows.Forms.Screen monitor)
		{
			_host = host;
			_monitor = monitor;
			_monitorPx = monitor.Bounds;
			_workPx = monitor.WorkingArea;
			_workDip = MonitorLayout.ToDip(monitor.WorkingArea);

			_sidebarDragGhost = new SidebarDragGhost(_sceneTransitionAnimator);
			_debugZoneOverlay = new DebugZoneOverlay(_sceneTransitionAnimator);

			// Load initial setting BEFORE UI initialization
			_hideDesktopIcons = Settings.GetHideDesktopIcons();

			InitializeComponent();

			// The icons ride above this sidebar however it gets raised.
			_iconOverlay.Owner = this;

			// Set DataContext AFTER setting is loaded
			DataContext = this;

			Settings.Changed += OnSettingChanged;

			_overlapCheckTimer = new Timer(OverlapCheck, null, 2500, TIMERINTERVAL_MILLISECONDS);

			SwitchSceneCommand = new ActionCommand(async model =>
			{
				if (_filterProcessKey != null)
					ClearAppFilter();
				var sceneModel = (SceneModel)model;
				await AnimatedSwitchTo(sceneModel.Scene);
			});

			_iconOverlay.OnIconClicked = ToggleAppFilter;
		}

		private void ToggleAppFilter(string processKey)
		{
			var prior = _filterProcessKey;
			_filterProcessKey = (prior == processKey) ? null : processKey;
			_iconOverlay.HighlightedProcessKey = _filterProcessKey;

			var action = prior == null ? "SET"
				: _filterProcessKey == null ? "CLEAR (toggle off)"
				: "SWAP";
			Log.Info("FILTER", $"ToggleAppFilter: action={action} prior='{prior ?? "<none>"}' new='{_filterProcessKey ?? "<none>"}'");

			AnimateSyncVisibility();
		}

		private void ClearAppFilter()
		{
			if (_filterProcessKey == null) return;
			Log.Info("FILTER", $"ClearAppFilter: cleared filter='{_filterProcessKey}'");
			_filterClearedAt = DateTime.Now;
			_filterProcessKey = null;
			_iconOverlay.HighlightedProcessKey = null;
			AnimateSyncVisibility();
		}

		private async Task<bool> AnimatedSwitchTo(Scene scene)
		{
			// Block while a transition or drag is in flight
			if (_sceneTransitionAnimator.IsAnimating || _sidebarDragGhost.IsActive || IsSidebarDragging || (_dragDropManager?.IsDragging ?? false))
			{
				Log.Info("TRANSITION", $"BLOCKED: switch to '{scene?.Title}' while animation/drag in progress");
				return false;
			}

			if (SceneManager.IsCurrentScene(scene))
			{
				Log.Info("TRANSITION", $"Already on '{scene?.Title}', skipping");
				return false;
			}

			// The window is on stage already (brought over from another display, raised from the
			// tray). The flight would redraw the outgoing scene on top of it and then fly a card
			// in over the real window — the flicker. Only the bookkeeping is left to do.
			if (scene is not null && SceneManager.IsOnScreen(scene))
			{
				Log.Info("TRANSITION", $"'{scene.Title}' is already on screen, switching without flight");
				_filterIconGen++;
				return await SceneManager.SwitchTo(scene);
			}

			// Cancel pending deferred icon refresh — scene-switch flips IsVisible on the
			// destination, a late UpdateIcons would render an empty tile.
			_filterIconGen++;

			var sceneModel = AllScenes.FirstOrDefault(s => s?.Id == scene?.Id);
			if (sceneModel == null)
			{
				Log.Info("TRANSITION", $"No sidebar model for '{scene?.Title}', instant switch");
				return await SceneManager.SwitchTo(scene);
			}

			var dpi = Dpi;
			Log.Action($"Scene switch: '{_removedCurrentScene?.Title ?? "(none)"}' → '{sceneModel.Title}' | scenes={Scenes.Count} dpi={dpi.X:F2},{dpi.Y:F2}");

			SceneManager.RestoreMinimizedInvisibly(sceneModel.Scene);

			// A scene picked from the "More" list has no tile on the strip — its card flies out
			// of (and the outgoing one back into) the More button instead of a collapsed slot.
			var fromMoreList = !sceneModel.IsVisible && sceneModel != _removedCurrentScene;
			var sidebarSlot = fromMoreList ? GetElementScreenBounds(moreButton) : GetSceneThumbnailScreenBounds(sceneModel);
			var incomingTarget = GetSceneWindowBounds(sceneModel);
			Log.Info("TRANSITION", $"Bounds: sidebarSlot={sidebarSlot} incomingTarget={incomingTarget}");

			var outgoingModel = _removedCurrentScene;
			var outgoingSource = Rect.Empty;
			if (outgoingModel != null)
				outgoingSource = GetCurrentSceneWindowBounds();
			Log.Info("TRANSITION", $"Outgoing: model={outgoingModel != null} source={outgoingSource}");

			// Deliberately NOT run yet. Every step here takes real content off the screen,
			// and the flying cards that replace it are not on screen until the animator has
			// arranged them and their capture has a frame. Running this first is what left
			// the stage blank for two frames and the tray tile for one; the animator calls
			// it back at the moment the cards actually cover what it hides.
			void HideWhatTheCardsCover()
			{
				if (outgoingSource != Rect.Empty)
				{
					Log.Info("TRANSITION", "Pre-hiding current scene windows");
					SceneManager.HideCurrentSceneWindows();
				}

				// The clicked tile is about to be hidden and the tray rebuilt around it, so no
				// tile still under the cursor will get its MouseLeave. Unwind hover state now,
				// while the transforms are still reachable.
				ResetAllSceneHoverTransforms();

				// A scene from the More list is already off the strip; reserving space for it
				// would open a gap in the tray for the length of the flight.
				if (!fromMoreList)
				{
					Log.Info("TRANSITION", $"Hiding sidebar item '{sceneModel.Title}' (reserving space)");
					sceneModel.IsHiddenButReserved = true;
					sceneModel.IsVisible = false;
				}
			}

			// The mirror image of the above: this is what puts the real content back, and it
			// runs while the cards are still parked on their targets. It also waits out the
			// tray tile the switch rebuilds for the outgoing scene — that tile starts a fresh
			// capture session, and dropping the outgoing card before its first frame just
			// moves the blank frame from the stage to the tray.
			var switched = false;
			async Task ShowWhatTheCardsUncover()
			{
				Log.Info("TRANSITION", "Calling SwitchTo");
				switched = await SceneManager.SwitchTo(scene);
				if (!switched)
				{
					Log.Info("TRANSITION", "SwitchTo blocked, restoring sidebar state");
					SyncVisibilityByUpdatedTimeStamp();
				}
				Log.Info("TRANSITION", $"SwitchTo completed, switched={switched} scenes={Scenes.Count}");

				if (outgoingModel is null) return;

				// The tile does not exist until the tray rebuild raised by SwitchTo has been
				// laid out, so let that settle before looking for it.
				await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
				var outgoingTile = FindSceneThumbnail(outgoingModel, outgoingModel.Windows.FirstOrDefault()?.Handle ?? IntPtr.Zero);
				await SceneTransitionAnimator.WaitForAsync(
					() => outgoingTile?.IsPresentable ?? true, "outgoing tray tile");
			}

			if (sidebarSlot != Rect.Empty && incomingTarget != Rect.Empty)
			{
				Log.Info("TRANSITION", "Starting animation");
					// A collapsed tile has no running capture to lend; the animator falls back to
					// an icon card, which is what the More list shows anyway.
					var incomingTile = fromMoreList ? null : FindSceneThumbnail(sceneModel, sceneModel.Windows.FirstOrDefault()?.Handle ?? IntPtr.Zero);
					var outgoingHandle = outgoingModel?.Windows.FirstOrDefault()?.Handle ?? IntPtr.Zero;
				await _sceneTransitionAnimator.AnimateSceneTransitionAsync(
					GetWorkAreaBounds(),
					sidebarSlot, incomingTarget, sceneModel, incomingTile,
					outgoingSource, sidebarSlot, outgoingModel, outgoingHandle,
						dpi, SidebarThumbCornerRadius, HideWhatTheCardsCover, ShowWhatTheCardsUncover);
				Log.Frame("TRANSITION", "Animation completed");
			}
			else
			{
				// No cards, so nothing to hide behind and nothing to wait for.
				HideWhatTheCardsCover();
				await ShowWhatTheCardsUncover();
			}

			return switched;
		}

		/// <summary>
		/// "Click wallpaper to reveal desktop", animated: the stage window flies into the strip
		/// while the rest of the scene is put away under it. The tile it becomes is reserved in
		/// the strip first, so the card lands exactly where the tile then appears.
		/// </summary>
		private async Task<bool> AnimatedShowDesktopAsync()
		{
			if (SceneManager.AnimatedSwitchInProgress?.Invoke() == true || SceneManager.IsDesktopView)
				return false;

			var outgoingModel = _removedCurrentScene;
			var outgoingWindow = SceneManager.GetCurrentWindows().FirstOrDefault(w => !w.IsMinimized);
			var source = WindowToLogicalRect(outgoingWindow);
			if (outgoingModel is null || outgoingWindow is null || source == Rect.Empty)
				return await SceneManager.SwitchTo(null);

			Log.Action($"Reveal desktop: '{outgoingModel.Title}' → strip");
			var switched = false;

			Rect CardReady()
			{
				// Everything below is under the card now: put the scene away, let the strip take
				// the tile in (reserved, invisible) and report where it sits once docked.
				SceneManager.HideCurrentSceneWindows();
				switched = SceneManager.SwitchTo(null).GetAwaiter().GetResult();
				if (!switched) return Rect.Empty;

				outgoingModel.IsHiddenButReserved = true;
				outgoingModel.IsVisible = false;
				UpdateLayout();
				return GetDockedSceneBounds(outgoingModel);
			}

			async Task Landed()
			{
				outgoingModel.IsHiddenButReserved = false;
				SyncVisibilityByUpdatedTimeStamp();
				// Hold the card until the tile's own capture has a frame, or the handover blinks.
				await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
				var tile = FindSceneThumbnail(outgoingModel, outgoingWindow.Handle);
				await SceneTransitionAnimator.WaitForAsync(() => tile?.IsPresentable ?? true, "desktop tray tile");
				RefreshIconOverlay();
			}

			await _sceneTransitionAnimator.AnimateToTrayAsync(
				GetWorkAreaBounds(), source, outgoingModel, outgoingWindow.Handle,
				Dpi, SidebarThumbCornerRadius, CardReady, Landed);

			return switched;
		}

		/// <summary>
		/// Where a scene's tile sits with the sidebar docked, in logical screen units — valid
		/// while the sidebar is still sliding in, since it is measured against the docked origin
		/// rather than the window's current Left.
		/// </summary>
		private Rect GetDockedSceneBounds(SceneModel scene)
		{
			if (TryGetSceneItemContainer(scene) is not FrameworkElement c || c.ActualWidth <= 0)
				return Rect.Empty;
			var tl = c.TranslatePoint(new Point(0, 0), this);
			return new Rect(DockedLeft + tl.X, DockedTop + tl.Y, c.ActualWidth, c.ActualHeight);
		}

		protected override void OnInitialized(EventArgs e)
		{
			base.OnInitialized(e);

			_thisHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
			_lastWidth = Width;
			_canSlide = !MonitorLayout.HasLeftNeighbour(_monitor, (int)Math.Ceiling(Width * MonitorLayout.PixelsPerDip));
			_edgeTriggerPx = _canSlide && _workPx.Left == _monitorPx.Left ? EdgeTouchPx : EdgeBandPx;
			_lastDpiX = MonitorLayout.PixelsPerDip;

			// Start hidden AND parked off-screen. Opacity=0 hides WPF content but DWM live thumbnails
			// follow window position regardless of opacity — parking keeps them culled by DWM
			// during setup so the user only sees the final slide-in.
			Opacity = 0;
			Left = ParkedLeft;
			Top = DockedTop;
			Height = _workDip.Height;

			StartHook();
		}

		/// <summary>
		/// Everything that talks to WinRT must be torn down HERE, not in OnClosed.
		/// OnClosing runs before WM_CLOSE destroys the window; OnClosed and the
		/// IsVisible cascade it triggers run *inside* the input-synchronous WM_CLOSE
		/// dispatch, where COM refuses outgoing cross-apartment calls with
		/// RPC_E_CANTCALLOUT_ININPUTSYNCCALL (0x8001010D). That throw used to kill the
		/// process mid-teardown, stranding every still-running GraphicsCaptureSession
		/// so DWM kept capturing those windows with nothing consuming the frames.
		/// </summary>
		protected override void OnClosing(CancelEventArgs e)
		{
			base.OnClosing(e);
			if (e.Cancel) return;

			// Stops the overlap check that may still be queued on the dispatcher.
			_closing = true;

			// Order matters: overlay/ghost first (they may hold a tile's borrowed
			// visual), then the tiles' own sessions.
			try { _sceneTransitionAnimator?.Dispose(); }
			catch (Exception ex) { Log.Info("SHUTDOWN", $"Animator dispose threw: {ex.Message}"); }

			try { _sidebarDragGhost?.Hide(); }
			catch (Exception ex) { Log.Info("SHUTDOWN", $"Drag ghost hide threw: {ex.Message}"); }

			try { CompositionThumbnail.ShutdownAll(); }
			catch (Exception ex) { Log.Info("SHUTDOWN", $"Capture shutdown threw: {ex.Message}"); }
		}

		protected override void OnClosed(EventArgs e)
		{
			// Cancel all background operations
			_cancellationTokenSource?.Cancel();
			_cancellationTokenSource?.Dispose();

			// Unsubscribe from SceneManager events before stopping to prevent memory leaks
			SceneManager.SceneChanged -= SceneManager_SceneChanged;
			SceneManager.CurrentSceneSelectionChanged -= SceneManager_CurrentSceneSelectionChanged;
			SceneManager.WindowsManager.WindowUpdated -= OnWindowUpdatedForDrag;

			StopHook();

			// Dispose the overlap check timer to stop background operations
			_overlapCheckTimer?.Dispose();

			Settings.Changed -= OnSettingChanged;

			// Dispose SceneManager properly
			SceneManager.Dispose();

			// Clean up animation overlay, drag ghost, and icon overlay
			_sceneTransitionAnimator?.Dispose();
			_iconOverlay?.Dispose();
			_debugZoneOverlay?.Hide();

			base.OnClosed(e);

			_host.OnSidebarClosed(this);
		}

		protected override async void OnContentRendered(EventArgs e)
		{
			base.OnContentRendered(e);

			Log.Info("STARTUP", "MainWindow content rendered, initializing...");

			// Shared across sidebars. With a single display there is nothing to split, so that
			// SceneManager keeps the display-agnostic behaviour it always had.
			var windowsManager = _host.WindowsManager;
			var monitorFilter = System.Windows.Forms.Screen.AllScreens.Length > 1 ? _monitor.DeviceName : null;
			SceneManager = new SceneManager(windowsManager, _hideDesktopIcons, monitorFilter, Settings.GetShowWindowsOneAtATime());
			SceneManager.IsAppFilterActive = () =>
				_filterProcessKey != null
				|| (DateTime.Now - _filterClearedAt) < _filterClearGrace;

			// Ensure SceneManager.Start() is called on the main thread
			if (Dispatcher.CheckAccess())
			{
				await SceneManager.Start();
			}
			else
			{
				await Dispatcher.InvokeAsync(async () => await SceneManager.Start());
			}

			SceneManager.SceneChanged += SceneManager_SceneChanged;
			SceneManager.CurrentSceneSelectionChanged += SceneManager_CurrentSceneSelectionChanged;
			SceneManager.AnimatedSwitch = scene => Dispatcher.InvokeAsync(() => AnimatedSwitchTo(scene)).Task.Unwrap();
			SceneManager.AnimatedShowDesktop = () => Dispatcher.InvokeAsync(AnimatedShowDesktopAsync).Task.Unwrap();
			SceneManager.AnimatedSwitchInProgress = () =>
				_sceneTransitionAnimator.IsAnimating || _sidebarDragGhost.IsActive || IsSidebarDragging || (_dragDropManager?.IsDragging ?? false);

			// Wire up drag-and-drop manager
			_dragDropManager = new DragDropManager(
				SceneManager,
				_sidebarDragGhost,
				() => Dpi,
				() => _workPx.Left,
				() => _lastWidth,
				() => GetWorkAreaBounds(),
				w => WindowToLogicalRect(w),
				w => AllScenes.OfType<SceneModel>().SelectMany(s => s.Windows).FirstOrDefault(wm => wm.Handle == w.Handle)?.Icon,
				() => { _suppressNextModeSlide = true; SyncVisibilityByUpdatedTimeStamp(); },
				SidebarThumbCornerRadius,
				() => !_canSlide);
			SceneManager.WindowsManager.WindowUpdated += OnWindowUpdatedForDrag;

			AddInitialScenes();

			// Pre-create the overlay window so the first animation has no HWND-creation lag
			_sceneTransitionAnimator.WarmUp(GetWorkAreaBounds());
			ShowDebugDragZones();

			// Initialize cancellation token source for background operations
			_cancellationTokenSource = new CancellationTokenSource();

			// Schedule a late initialization pass to recalculate thumbnail sizes after all window information is available.
			_ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(2000, _cancellationTokenSource.Token).ConfigureAwait(false);
					if (!_cancellationTokenSource.Token.IsCancellationRequested)
					{
						Dispatcher.Invoke(() =>
						{
							foreach (var scene in Scenes)
								scene.UpdatePreviewSizes();
						});
					}
				}
				catch (OperationCanceledException)
				{
					// Expected during shutdown, ignore
				}
			});

			// The display holding the foreground window opens on that window's scene. Every other
			// display opens on its frontmost scene, without taking focus from where the user is —
			// including a single managed display whose foreground window is on an unmanaged one.
			// Leaving it without a scene kept every window on screen until the first click.
			var foreground = Win32.GetForegroundWindow();
			var foregroundScene = SceneManager.FindSceneForWindow(foreground);
			if (foregroundScene is object)
				await SceneManager.SwitchTo(foregroundScene).ConfigureAwait(true);
			else if (SceneManager.FindTopmostScene() is Scene topmost)
				await SceneManager.SwitchTo(topmost, focus: false).ConfigureAwait(true);

			// All icon-overlay enable, position calc, opacity reveal, and slide-in animations happen
			// inside a single Loaded-priority dispatcher slot. Window stays at Left=-Width with
			// Opacity=0 throughout setup, so the user only sees the slide-in.
			_ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
			{
				var startupDuration = TimeSpan.FromSeconds(0.5);
				var startupEasing = new PowerEase { EasingMode = EasingMode.EaseOut };

				// Force layout pass so ItemContainerGenerator has produced visual containers.
				// Coords are computed relative to the window (TranslatePoint, not PointToScreen)
				// since the HWND is parked at Left=-Width.
				UpdateLayout();

				_iconOverlay.Enabled = true;
				var visible = Scenes.Where(s => s.IsVisible).ToList();
				// Window is parked off-screen during setup, so PointToScreen would offset icon
				// coords by the park distance. Use TranslatePoint (window-relative) plus the
				// docked origin the window is about to slide to.
				_iconOverlay.UpdateIcons(visible, sm =>
				{
					var c = scenesControl.ItemContainerGenerator.ContainerFromItem(sm) as FrameworkElement;
					if (c == null) return Rect.Empty;
					var tl = c.TranslatePoint(new Point(0, 0), this);
					return new Rect(DockedLeft + tl.X, DockedTop + tl.Y, c.ActualWidth, c.ActualHeight);
				}, GetWorkAreaBounds());

				Opacity = 1;

				if (!_canSlide)
				{
					// A neighbour on the left would see the slide pass across it — appear in place.
					_iconOverlay.SlideIn(0, TimeSpan.Zero, startupEasing);
					Left = DockedLeft;
					_startupSlideComplete = true;
					return;
				}

				// Reveal: alpha→1 while still off-screen, then animate Left from parked to docked in
				// lockstep with the icon-overlay canvas RenderTransform (same duration + easing).
				_iconOverlay.SlideIn(-Width, startupDuration, startupEasing);

				var slideIn = new DoubleAnimationUsingKeyFrames { Duration = new Duration(startupDuration) };
				slideIn.KeyFrames.Add(new EasingDoubleKeyFrame(ParkedLeft, KeyTime.FromPercent(0)));
				slideIn.KeyFrames.Add(new EasingDoubleKeyFrame(DockedLeft, KeyTime.FromPercent(1.0), startupEasing));
				slideIn.Completed += (_, _) => _startupSlideComplete = true;
				BeginAnimation(LeftProperty, slideIn);
			});
		}

		private void AddInitialScenes()
		{
			var initialScenes = SceneManager.GetScenes().ToArray();
			Log.Info("STARTUP", $"Adding {initialScenes.Length} initial scenes to sidebar");
			for (int i = 0; i < initialScenes.Length; i++)
			{
				var model = SceneModel.FromScene(initialScenes[i]);
				model.IsVisible = i <= _maxScenes; // i is zero based, so it should be i+1 but one scene gets selected (and removed from the sidebar) that makes i+0 again
				Scenes.Add(model);
				Log.Info("STARTUP", $"  Scene[{i}]: '{model.Title}' visible={model.IsVisible} windows={model.Windows.Count}");
			}

			UpdateMoreButton();
			RefreshIconOverlay();
		}

		private void SceneManager_CurrentSceneSelectionChanged(object? sender, CurrentSceneSelectionChangedEventArgs args)
		{
			// Ensure we are on the UI/Dispatcher thread before mutating observable collections bound to UI
			if (!Dispatcher.CheckAccess())
			{
				Dispatcher.Invoke(() => SceneManager_CurrentSceneSelectionChanged(sender, args));
				return;
			}

			// Re-entrancy: every mutation below raises CollectionChanged synchronously,
			// and its handlers can drive SceneManager back into another selection
			// change. A nested pass would edit Scenes / _removedCurrentScene on a
			// half-applied state — post it to run after this one instead.
			if (_inSelectionChange)
			{
				Log.Info("SIDEBAR", "SelectionChanged re-entered, deferring to next dispatcher slot");
				Dispatcher.BeginInvoke(new Action(() => SceneManager_CurrentSceneSelectionChanged(sender, args)));
				return;
			}

			_inSelectionChange = true;
			try { ApplyCurrentSceneSelection(args); }
			finally { _inSelectionChange = false; }
		}

		private void ApplyCurrentSceneSelection(CurrentSceneSelectionChangedEventArgs args)
		{
			var currentModel = args.Current is null ? null : Scenes.FirstOrDefault(m => m.Id == args.Current.Id);
			Log.Info("SIDEBAR", $"SelectionChanged: current='{args.Current?.Title ?? "(null)"}' prior='{args.Prior?.Title ?? "(null)"}' removedCurrent='{_removedCurrentScene?.Title ?? "(null)"}' scenes={Scenes.Count}");

			if (currentModel is object)
			{
				var currentIndex = Scenes.IndexOf(currentModel);
				Log.Info("SIDEBAR", $"Removing '{currentModel.Title}' at index {currentIndex}, inserting '{_removedCurrentScene?.Title ?? "(null)"}'");
				currentModel.IsHiddenButReserved = false;
				if (currentIndex >= 0)
					Scenes.RemoveAt(currentIndex);

				// RemoveAt raised CollectionChanged synchronously, and its handlers
				// (container unload → TeardownSession → …) can mutate Scenes again,
				// so currentIndex may now be past the end. Clamp instead of trusting
				// the captured index — this threw ArgumentOutOfRangeException.
				if (_removedCurrentScene is object && !Scenes.Contains(_removedCurrentScene))
				{
					var insertAt = Math.Clamp(currentIndex < 0 ? Scenes.Count : currentIndex, 0, Scenes.Count);
					if (insertAt != currentIndex)
						Log.Info("SIDEBAR", $"Insert index {currentIndex} stale after removal, clamped to {insertAt} (count={Scenes.Count})");
					Scenes.Insert(insertAt, _removedCurrentScene);
				}
			}
			else
			{
				Log.Info("SIDEBAR", $"Current not found in Scenes, appending '{_removedCurrentScene?.Title ?? "(null)"}'");
				// Contains guard: a re-entrant pass may already have put it back, and
				// a duplicated item breaks ItemContainerGenerator.
				if (_removedCurrentScene is object && !Scenes.Contains(_removedCurrentScene))
					Scenes.Add(_removedCurrentScene);
			}

			_removedCurrentScene = currentModel;
			Log.Info("SIDEBAR", $"State: _removedCurrentScene='{_removedCurrentScene?.Title ?? "(null)"}' scenes={Scenes.Count} visible={Scenes.Count(s => s.IsVisible)}");

			SyncVisibilityByUpdatedTimeStamp();

			// Desktop view: stow scene windows but keep the scene tray visible.
			if (args.Current is null && _showRecentApps)
			{
				Mode = WindowMode.OnScreen;
			}

			RefreshIconOverlay();
		}

		protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
		{
			base.OnRenderSizeChanged(sizeInfo);
			// Stay parked off-screen until the startup slide-in animation finishes; otherwise this
			// firing during the initial layout pass yanks the window back to its docked spot and
			// leaks the pre-slide visual state (scenes appearing, active scene removal, icon flicker).
			// Likewise while stowed: docking a stowed strip here put it over a maximized window.
			// The geometry is this sidebar's own display, not whichever one the parked window
			// happens to be nearest — for a display with a left neighbour that is the neighbour.
			this.Left = _startupSlideComplete && Mode != WindowMode.OffScreen ? DockedLeft : ParkedLeft;
			this.Top = DockedTop;
			this.Height = _workDip.Height;
			RefreshIconOverlay();
		}

		private void SceneManager_SceneChanged(object? sender, SceneChangedEventArgs e)
		{
			this.Dispatcher.Invoke(() =>
			{
				Log.Info("UI", $"SceneChanged: {e.Change} scene='{e.Scene.Title}'");

				switch (e.Change)
				{
					case ChangeType.Created:
						Scenes.Add(SceneModel.FromScene(e.Scene));
						SyncVisibilityByUpdatedTimeStamp();
						break;
					case ChangeType.Updated:
						if (AllScenes.FirstOrDefault(s => s?.Id == e.Scene.Id) is SceneModel toUpdate)
							toUpdate.UpdateFromScene(e.Scene);
						break;
					case ChangeType.Removed:
						if (AllScenes.FirstOrDefault(s => s?.Id == e.Scene.Id) is SceneModel toRemove)
						{
							if (toRemove.Equals(_removedCurrentScene))
								_removedCurrentScene = null;
							else
								Scenes.Remove(toRemove);
						}
						SyncVisibilityByUpdatedTimeStamp();
						break;
				}

				RefreshIconOverlay();
			});
		}

		private void OnWindowUpdatedForDrag(IWindow window, WindowUpdateType type)
		{
			switch (type)
			{
				case WindowUpdateType.MoveStart:
					Dispatcher.Invoke(() => _dragDropManager?.OnWindowMoveStart(window));
					break;
				case WindowUpdateType.MoveEnd:
					Dispatcher.Invoke(() => _dragDropManager?.OnWindowMoveEnd(window));
					break;
				case WindowUpdateType.Move:
					Dispatcher.Invoke(() => _dragDropManager?.OnWindowMoved(window));
					break;
			}
		}

		private void OnMousePressed(object? sender, MouseHookEventArgs e)
		{
			if (TrayController.IsMenuOpen)
				return;

			// Filter-clear gate runs before the foreground guard: outside-clicks (desktop, other apps)
			// move foreground away from us, so the guard below would otherwise miss them.
			if (_filterProcessKey != null)
			{
				var screenPoint = new Point(e.Data.X, e.Data.Y);
				Dispatcher.Invoke(() =>
				{
					if (_filterProcessKey != null && !IsPointInsideAnyVisibleScene(screenPoint))
						ClearAppFilter();
				});
			}

			var foregroundWindow = Win32.GetForegroundWindow();
			if (foregroundWindow != _thisHandle)
				return;

			if (EnableWindowPullToScene)
			{
				var screenPoint = new Point(e.Data.X, e.Data.Y);
				this.Dispatcher.Invoke(() =>
				{
					_mouseDownScene = FindSceneByPoint(screenPoint);
				});
			}
		}

		private void OnMouseReleased(object? sender, MouseHookEventArgs e)
		{
			if (TrayController.IsMenuOpen)
				return;

			// A stage→tray drag that has taken over from the OS move loop has no MOVESIZEEND
			// left to end it, so the release has to come from here. It owns this event when
			// it takes it — nothing below may also act on it.
			if (_dragDropManager is object)
			{
				var x = e.Data.X;
				var y = e.Data.Y;
				bool consumed = Dispatcher.Invoke(() => _dragDropManager.OnGlobalMouseUp(x, y));
				if (consumed)
				{
					_mouseDownScene = null;
					return;
				}
			}

			if (EnableWindowPullToScene)
			{
				// WPF drag is handled by ScenesControl_PreviewMouseLeftButtonUp — only legacy pull remains.
				// e.Data.X is a hook coordinate: physical pixels. _lastWidth is the sidebar's WPF
				// width: DIPs. Comparing them directly made everything right of half the sidebar
				// count as "released outside" at 200% scale, so an ordinary click on the right of a
				// tile pulled its window onto the stage instead of switching to it.
				var sidebarRightPhysical = _workPx.Left + SidebarWidthPx;
				if (!IsSidebarDragging && e.Data.X > sidebarRightPhysical && _mouseDownScene is object)
				{
					Log.Info("DRAG", $"Pulled window from scene '{_mouseDownScene.Title}' (mouseX={e.Data.X} > sidebarRight={sidebarRightPhysical})");
					this.Dispatcher.Invoke(() =>
					{
						SceneManager.PopWindowFrom(_mouseDownScene.Scene).SafeFireAndForget();
					});
				}
				_mouseDownScene = null;
			}
		}

		private SceneModel? FindSceneByPoint(Point p)
		{
			var thisWindow = new WindowsWindow(_thisHandle);
			var pointOnWindow = new Point(p.X - thisWindow.Location.X, p.Y - thisWindow.Location.Y);

			var dpi = Dpi;

			pointOnWindow.X /= dpi.X;
			pointOnWindow.Y /= dpi.Y;

			SceneModel? model = null;

			var element = VisualTreeHelper.HitTest(this, pointOnWindow)?.VisualHit;

			while (element is not null)
			{
				if (element is FrameworkElement { DataContext: SceneModel m })
				{
					model = m;
					break;
				}

				element = element.GetParentObject();
			}

			return model;
		}

		private bool IsPointInsideAnyVisibleScene(Point screenPoint)
		{
			foreach (var scene in Scenes)
			{
				if (!scene.IsVisible) continue;
				var rect = GetSceneThumbnailScreenBounds(scene);
				if (rect != Rect.Empty && rect.Contains(screenPoint))
					return true;
			}
			return false;
		}

		#region WPF Sidebar Drag (Flow 2: sidebar → active)

		private const double WpfDragThreshold = 10.0;

		private void ScenesControl_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
		{
			if (_sceneTransitionAnimator.IsAnimating || _sidebarDragGhost.IsActive) return;

			// Find which SceneModel was clicked
			var hit = e.OriginalSource as FrameworkElement;
			SceneModel? scene = null;
			while (hit != null)
			{
				if (hit.DataContext is SceneModel sm) { scene = sm; break; }
				hit = VisualTreeHelper.GetParent(hit) as FrameworkElement;
			}
			if (scene == null) return;

			_wpfDragScene = scene;
			_wpfDragStartPoint = e.GetPosition(this);
			_sidebarDragPhase = SidebarDragPhase.None;
			_sidebarDragWindow = null;
			Log.Info("DRAG", $"WPF mousedown on '{scene.Title}' at ({_wpfDragStartPoint.X:F0},{_wpfDragStartPoint.Y:F0})");
		}

		private void ScenesControl_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
		{
			if (_wpfDragScene == null) return;
			if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
			{
				CancelWpfDrag();
				return;
			}

			var pos = e.GetPosition(this);

			if (!IsSidebarDragging)
			{
				var dx = pos.X - _wpfDragStartPoint.X;
				var dy = pos.Y - _wpfDragStartPoint.Y;
				if (Math.Sqrt(dx * dx + dy * dy) < WpfDragThreshold) return;

				_sidebarDragPhase = SidebarDragPhase.InSidebar;
				Mouse.Capture(scenesControl, System.Windows.Input.CaptureMode.SubTree);
				Log.Info("DRAG", $"WPF drag started from '{_wpfDragScene.Title}'");

				// This press is the WPF drag's now. The hook's legacy pull reads the same press
				// on its own thread, and when the button-up reaches WPF first the drag is already
				// over by the time the hook looks — so it pulled a second window after a drop,
				// or pulled one after a drop the user cancelled in the buffer.
				_mouseDownScene = null;

				// Resolve the window that will be popped (same as PopWindowFrom picks)
				_sidebarDragWindow = _wpfDragScene.Scene.Windows.LastOrDefault();

				// Compute rects for interpolation
				_sidebarDragThumbRect = GetSceneThumbnailScreenBounds(_wpfDragScene);
				_sidebarDragWindowRect = _sidebarDragWindow != null
					? WindowToLogicalRect(_sidebarDragWindow)
					: Rect.Empty;
				if (_sidebarDragWindowRect == Rect.Empty)
					_sidebarDragWindowRect = new Rect(0, 0, 800, 600);

				_sidebarDragDpi = Dpi;
				_sidebarDragBufferLeft = _lastWidth;
				_sidebarDragBufferRight = _lastWidth + DragDropManager.BufferWidthLogical;

				var overlayBounds = GetWorkAreaBounds();
				if (_sidebarDragThumbRect != Rect.Empty && overlayBounds != Rect.Empty)
					_sidebarDragGhost.Show(overlayBounds, _sidebarDragThumbRect, _wpfDragScene,
						FindSceneThumbnail(_wpfDragScene, _sidebarDragWindow?.Handle ?? IntPtr.Zero),
						_sidebarDragDpi, SidebarThumbCornerRadius);
				else
					Log.Info("DRAG", $"Ghost skipped: overlay={overlayBounds == Rect.Empty} thumb={_sidebarDragThumbRect == Rect.Empty}");
			}

			if (!IsSidebarDragging) return;

			var screenPos = PointToScreen(pos);
			var dpi = _sidebarDragDpi;
			double cursorLogicalX = screenPos.X / dpi.X;
			double cursorLogicalY = screenPos.Y / dpi.Y;

			var prevPhase = _sidebarDragPhase;

			// Transition back from PastBuffer: hide real window, restore ghost
			if (_sidebarDragPhase == SidebarDragPhase.PastBuffer && pos.X <= _sidebarDragBufferRight)
			{
				HideSidebarDragRealWindow();
				_sidebarDragGhost.SetVisible(true);
				Log.Info("DRAG", $"Phase: PastBuffer → {(pos.X <= _sidebarDragBufferLeft ? "InSidebar" : "InBuffer")}");
			}

			if (pos.X <= _sidebarDragBufferLeft)
			{
				_sidebarDragPhase = SidebarDragPhase.InSidebar;

				_sidebarDragGhost.UpdatePositionAndSize(
					cursorLogicalX - _sidebarDragThumbRect.Width / 2,
					DragDropManager.CardTopFor(cursorLogicalY, _sidebarDragThumbRect.Height),
					_sidebarDragThumbRect.Width,
					_sidebarDragThumbRect.Height,
						CompositionThumbnail.TrayTiltDegrees);
			}
			else if (pos.X <= _sidebarDragBufferRight)
			{
				_sidebarDragPhase = SidebarDragPhase.InBuffer;

				double t = Math.Clamp((pos.X - _sidebarDragBufferLeft) / DragDropManager.BufferWidthLogical, 0.0, 1.0);
				double ghostW = DragDropManager.Lerp(_sidebarDragThumbRect.Width, _sidebarDragWindowRect.Width, t);
				double ghostH = DragDropManager.Lerp(_sidebarDragThumbRect.Height, _sidebarDragWindowRect.Height, t);

				_sidebarDragGhost.UpdatePositionAndSize(
					cursorLogicalX - ghostW / 2,
					DragDropManager.CardTopFor(cursorLogicalY, ghostH),
					ghostW, ghostH,
						DragDropManager.Lerp(CompositionThumbnail.TrayTiltDegrees, 0.0, t));
			}
			else
			{
				// --- Past buffer zone: show real window ---
				bool entering = _sidebarDragPhase != SidebarDragPhase.PastBuffer;
				if (entering)
				{
					_sidebarDragGhost.SetVisible(false);
					_sidebarDragPhase = SidebarDragPhase.PastBuffer;
					Log.Info("DRAG", $"Phase: {prevPhase} → PastBuffer");
				}

				// Position window at cursor (physical pixels), then show on entry
				if (_sidebarDragWindow != null)
				{
					int winW = (int)(_sidebarDragWindowRect.Width * dpi.X);
					int winH = (int)(_sidebarDragWindowRect.Height * dpi.Y);
					int winX = (int)(screenPos.X - winW / 2.0);
					int winY = (int)(DragDropManager.CardTopFor(screenPos.Y / dpi.Y, _sidebarDragWindowRect.Height) * dpi.Y);
					Win32.SetWindowPos(_sidebarDragWindow.Handle, IntPtr.Zero,
						winX, winY, winW, winH,
						Win32.SetWindowPosFlags.DoNotActivate);
				}

				if (entering)
					ShowSidebarDragRealWindow();
			}
		}

		private void ScenesControl_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
		{
			if (IsSidebarDragging)
			{
				e.Handled = true; // Suppress SwitchSceneCommand

				var phase = _sidebarDragPhase;

				// Activate only when dropped past the buffer (outside tray + buffer).
				// InBuffer/InSidebar drops cancel — the scene stays stowed.
				if (phase == SidebarDragPhase.PastBuffer && _wpfDragScene != null)
				{
					Log.Info("DRAG", $"WPF drop (phase={phase}), pulling from '{_wpfDragScene.Title}'");
					var scene = _wpfDragScene.Scene;

					// The drag moved the real window to the cursor with a direct SetWindowPos, so the
					// position OpacityWindowStrategy saved when the window was first parked is stale.
					// Drop it: CancelWpfDrag re-parks the window, and that Hide now records where the
					// user let go, so the Show that follows the move puts it under the ghost.
					if (_sidebarDragWindow != null)
						Strategies.OpacityWindowStrategy.ForgetOriginalPosition(_sidebarDragWindow.Handle);

					CancelWpfDrag();
					SceneManager.PopWindowFrom(scene).SafeFireAndForget();
				}
				else
				{
					Log.Info("DRAG", $"WPF drag cancelled (phase={phase})");
					CancelWpfDrag();
				}
				return;
			}

			// Not dragging — let SwitchSceneCommand fire normally
			_wpfDragScene = null;
		}

		private void CancelWpfDrag()
		{
			// Restore alpha + original rect — WGC captures post-alpha, so an
			// alpha=0 window leaves the sidebar tile rendering empty.
			if (_sidebarDragWindow != null)
			{
				if (_sidebarDragPhase == SidebarDragPhase.PastBuffer)
					HideSidebarDragRealWindow();

				var sourceScene = _wpfDragScene?.Scene;
				if (sourceScene != null && SceneManager.IsCurrentScene(sourceScene)
					&& _sidebarDragWindowRect != Rect.Empty)
				{
					// Current (on-stage) scene: keep the window visible at its home rect.
					var dpi = _sidebarDragDpi;
					int x = (int)(_sidebarDragWindowRect.X * dpi.X);
					int y = (int)(_sidebarDragWindowRect.Y * dpi.Y);
					int w = (int)(_sidebarDragWindowRect.Width * dpi.X);
					int h = (int)(_sidebarDragWindowRect.Height * dpi.Y);
					Win32.SetWindowPos(_sidebarDragWindow.Handle, IntPtr.Zero,
						x, y, w, h, Win32.SetWindowPosFlags.DoNotActivate);
					// Back under the user's hands, so the layered style the drag added comes
					// off — that makes it opaque, and a Chromium window that keeps it renders blank.
					Win32Helper.ClearLayered(_sidebarDragWindow.Handle);
				}
				else
				{
					// Tray (stowed) scene: re-park off-screen so it stays hidden but the live
					// tile keeps capturing it. Parking also drops the drag's layered style, so
					// the window is opaque for WGC and Chromium keeps painting it.
					SceneManager.ParkWindow(_sidebarDragWindow);
				}
			}
			_sidebarDragGhost.Hide();
			_sidebarDragPhase = SidebarDragPhase.None;
			_wpfDragScene = null;
			_sidebarDragWindow = null;
			Mouse.Capture(null);
			SceneManager.WindowsManager.SuppressNextDesktopClick();
		}

		private void ShowSidebarDragRealWindow()
		{
			if (_sidebarDragWindow == null) return;
			if (_sidebarDragWindow.IsMinimized)
			{
				// Clear the mark before ShowNormal: dragging the tile out is the user asking
				// for the window back, and the MINIMIZEEND this call echoes must not be read
				// as a taskbar restore (which would switch scenes in the middle of the drag).
				SceneManager.ForgetUserMinimized(_sidebarDragWindow);
				Win32Helper.SetAlpha(_sidebarDragWindow.Handle, 0);
				_sidebarDragWindow.ShowNormal();
				Log.Info("DRAG", "Sidebar drag: restored minimized window");
			}
			// Opaque again: drops the layered style HideSidebarDragRealWindow (or the restore
			// above) added.
			Win32Helper.ClearLayered(_sidebarDragWindow.Handle);
			Win32.SetWindowPos(_sidebarDragWindow.Handle, Win32.HWND_TOPMOST,
				0, 0, 0, 0,
				Win32.SetWindowPosFlags.DoNotActivate | Win32.SetWindowPosFlags.IgnoreMove | Win32.SetWindowPosFlags.IgnoreResize);
			Log.Info("DRAG", "Sidebar drag: real window shown (alpha→255, topmost)");
		}

		private void HideSidebarDragRealWindow()
		{
			if (_sidebarDragWindow == null) return;
			Win32.SetWindowPos(_sidebarDragWindow.Handle, Win32.HWND_NOTOPMOST,
				0, 0, 0, 0,
				Win32.SetWindowPosFlags.DoNotActivate | Win32.SetWindowPosFlags.IgnoreMove | Win32.SetWindowPosFlags.IgnoreResize);
			Win32Helper.SetAlpha(_sidebarDragWindow.Handle, 0);
			Log.Info("DRAG", "Sidebar drag: real window hidden (alpha→0, topmost removed)");
		}

		#endregion

		private void SyncVisibilityByUpdatedTimeStamp()
		{
			var scenes = Scenes.OrderByDescending(s => s.Updated).ToArray();

			if (_filterProcessKey == null)
			{
				for (int i = 0; i < scenes.Length; i++)
					scenes[i].IsVisible = i < _maxScenes;
				Log.Info("FILTER", $"SyncVisibility: filter=<none> total={scenes.Length} shown={Math.Min(scenes.Length, _maxScenes)} (cap={_maxScenes})");
				AssignRowTilts();
				UpdateMoreButton();
				return;
			}

			int shown = 0, hidden = 0;
			foreach (var scene in scenes)
			{
				var processNames = scene.Windows.Select(w => w.Window?.ProcessFileName ?? "").ToArray();
				var match = processNames.Any(p => p == _filterProcessKey);
				scene.IsVisible = match;
				if (match) shown++; else hidden++;
				Log.Info("FILTER", $"SyncVisibility: scene='{scene.Title}' processes=[{string.Join(",", processNames)}] match={match} → IsVisible={match}");
			}
			Log.Info("FILTER", $"SyncVisibility: filter='{_filterProcessKey}' shown={shown} hidden={hidden} total={scenes.Length}");
			AssignRowTilts();
			UpdateMoreButton();
		}

		// Assigns each visible scene the top/bottom edge angles the macOS position
		// law dictates for its on-screen location (see EdgePerspectiveDistanceRatio).
		// Angles need final layout positions, so the real work runs at Loaded
		// priority — after the layout pass that the triggering data change caused.
		// Tilts are a composition-only transform (no layout input), so measuring
		// post-layout cannot feed back into another pass.
		private bool _tiltAssignQueued;
		private void AssignRowTilts()
		{
			if (_tiltAssignQueued) return;
			_tiltAssignQueued = true;
			Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
			{
				_tiltAssignQueued = false;
				AssignRowTiltsCore();
			}));
		}

		private void AssignRowTiltsCore()
		{
			// The law pivots on the centre of the display the tray is on, so measure positions
			// from that display's top and hand the law its height.
			var monitorDip = MonitorLayout.ToDip(_monitorPx);
			double screenH = monitorDip.Height;
			double centerY = screenH / 2.0;
			double d = screenH * EdgePerspectiveDistanceRatio;

			foreach (var scene in Scenes.Where(s => s.IsVisible))
			{
				var container = TryGetSceneItemContainer(scene);
				if (container is null || TryGetSceneInnerGrid(scene) is not FrameworkElement inner || inner.ActualHeight <= 0)
					continue;
				if (System.Windows.Media.VisualTreeHelper.GetParent(container) is not UIElement panel)
					continue;

				// Layout slot, not TransformToVisual: the FLIP slide animates the
				// container's RenderTransform from the OLD position to 0, so a visual
				// measurement mid-flight would bake in a stale offset that nothing
				// recomputes once the animation lands. The slot is already final here.
				var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(container);
				Point panelOrigin;
				try { panelOrigin = panel.TransformToVisual(this).Transform(new Point(0, 0)); }
				catch (InvalidOperationException) { continue; }

				double windowTop = double.IsNaN(Top) ? DockedTop : Top;
				double yTop = windowTop - monitorDip.Top + panelOrigin.Y + slot.Y;
				double yBottom = yTop + inner.ActualHeight;

				// centerY / d above are kept for the log line only — the geometry
				// measurement pass parses them. The angles themselves come from the
				// shared law so the flying card can solve its own from the same source.
				scene.TiltTopDegrees = SceneModel.EdgeTiltDegreesAt(yTop, screenH);
				scene.TiltBottomDegrees = SceneModel.EdgeTiltDegreesAt(yBottom, screenH);
				// Invariant format - parsed by the geometry measurement pass.
				Log.Info("TILT", FormattableString.Invariant($"scene='{scene.Title}' yTop={yTop:F1} yBottom={yBottom:F1} centerY={centerY:F1} d={d:F1} top={scene.TiltTopDegrees:F3} bottom={scene.TiltBottomDegrees:F3}"));
			}
		}

		// Animated counterpart to SyncVisibilityByUpdatedTimeStamp for filter-toggle paths.
		private async void AnimateSyncVisibility()
		{
			var iconGen = ++_filterIconGen;

			var scenes = Scenes.OrderByDescending(s => s.Updated).ToArray();
			bool[] target = new bool[scenes.Length];
			if (_filterProcessKey == null)
			{
				for (int i = 0; i < scenes.Length; i++) target[i] = i < _maxScenes;
			}
			else
			{
				for (int i = 0; i < scenes.Length; i++)
					target[i] = scenes[i].Windows.Any(w => w.Window?.ProcessFileName == _filterProcessKey);
			}

			// FLIP-First: capture each visible scene's container Y in window coords.
			double[] oldY = new double[scenes.Length];
			for (int i = 0; i < scenes.Length; i++)
				oldY[i] = scenes[i].IsVisible ? GetSceneInnerY(scenes[i]) : double.NaN;

			bool[] originalVisible = new bool[scenes.Length];
			for (int i = 0; i < scenes.Length; i++) originalVisible[i] = scenes[i].IsVisible;

			// Phase order matters: enter (IsVisible=true) before survivor measurement so the
			// FLIP From offset reflects post-enter layout.
			var pendingExits = new List<TaskCompletionSource>();
			int transitioning = 0;
			for (int i = 0; i < scenes.Length; i++)
			{
				var s = scenes[i];
				if (target[i] && !originalVisible[i]) { AnimateSceneEnter(s); transitioning++; }
			}
			UpdateLayout();
			for (int i = 0; i < scenes.Length; i++)
			{
				var s = scenes[i];
				if (target[i] && originalVisible[i] && !double.IsNaN(oldY[i]))
					RestoreSceneVisible(s, oldY[i]);
			}
			for (int i = 0; i < scenes.Length; i++)
			{
				var s = scenes[i];
				if (!target[i] && originalVisible[i])
				{
					var tcs = new TaskCompletionSource();
					pendingExits.Add(tcs);
					AnimateSceneExit(s, tcs);
					transitioning++;
				}
			}
			Log.Info("FILTER", $"AnimateSyncVisibility: filter='{_filterProcessKey ?? "<none>"}' transitioning={transitioning}");
			UpdateMoreButton();

			// Concurrent icon morph: kick off immediately so exiter icons fade out alongside their
			// scene fade (SET) and enterer icons fade in alongside their scene appearance (CLEAR).
			var stopwatch = System.Diagnostics.Stopwatch.StartNew();
			var visibleNow = scenes.Where((_, i) => target[i]).ToList();
			_iconOverlay.UpdateIcons(visibleNow, s => GetSceneThumbnailScreenBounds(s), GetWorkAreaBounds());
			Log.Info("FILTER_TIMING", $"first UpdateIcons at t={stopwatch.ElapsedMilliseconds}ms exits={pendingExits.Count} visibleNow={visibleNow.Count}");

			if (pendingExits.Count == 0) { AssignRowTilts(); return; }

			// SET path: layout doesn't collapse until exits complete; reposition survivor icons
			// against final layout afterward. Safety timeout covers SnapshotAndReplace swallowing Completed.
			var allExits = Task.WhenAll(pendingExits.Select(t => t.Task));
			var safety = Task.Delay(_filterMorphDuration.TimeSpan + TimeSpan.FromMilliseconds(50));
			await Task.WhenAny(allExits, safety);
			Log.Info("FILTER_TIMING", $"exits completed at t={stopwatch.ElapsedMilliseconds}ms");
			if (iconGen != _filterIconGen) return;
			UpdateLayout();
			var visibleAtFinal = Scenes.Where(s => s.IsVisible).ToList();
			AssignRowTilts();
			UpdateMoreButton();
			_iconOverlay.UpdateIcons(visibleAtFinal, s => GetSceneThumbnailScreenBounds(s), GetWorkAreaBounds());
			Log.Info("FILTER_TIMING", $"second UpdateIcons at t={stopwatch.ElapsedMilliseconds}ms visibleFinal={visibleAtFinal.Count}");
		}

		private void AnimateSceneEnter(SceneModel s)
		{
			var gen = NextFilterGen(s.Id);
			s.IsVisible = true;
			if (TryGetSceneInnerGrid(s) is FrameworkElement inner) { StartEnterAnimation(inner); return; }
			Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
			{
				if (CurrentFilterGen(s.Id) != gen) return;
				if (TryGetSceneInnerGrid(s) is FrameworkElement inner2)
					StartEnterAnimation(inner2);
			});
		}

		// From=0 explicit; FillBehavior.Stop reverts to style-base Opacity=0.8.
		private static void StartEnterAnimation(FrameworkElement inner) =>
			inner.BeginAnimation(UIElement.OpacityProperty,
				Anim.From(0, 0.8, _filterMorphDuration, _filterEaseOut, FillBehavior.Stop));

		private void AnimateSceneExit(SceneModel s, TaskCompletionSource? tcs = null)
		{
			var gen = NextFilterGen(s.Id);
			if (TryGetSceneInnerGrid(s) is not FrameworkElement inner)
			{
				s.IsVisible = false;
				tcs?.TrySetResult();
				return;
			}
			// HoldEnd keeps Opacity=0 painted until IsVisible flips — no flash before collapse.
			var opacityAnim = Anim.To(0, _filterMorphDuration, _filterEaseIn);
			opacityAnim.Completed += (_, _) =>
			{
				if (CurrentFilterGen(s.Id) == gen)
					s.IsVisible = false;
				tcs?.TrySetResult();
			};
			inner.BeginAnimation(UIElement.OpacityProperty, opacityAnim, HandoffBehavior.SnapshotAndReplace);
		}

		// FLIP target = ContentPresenter, not inner Grid. The Grid carries the SceneOpacity
		// style's ScaleTransform consumed by the hover storyboard — replacing its
		// RenderTransform would break `(RenderTransform).(ScaleTransform.ScaleX)` resolution.
		private void RestoreSceneVisible(SceneModel s, double oldYAbs = double.NaN)
		{
			NextFilterGen(s.Id);
			if (TryGetSceneInnerGrid(s) is not FrameworkElement inner) return;
			inner.BeginAnimation(UIElement.OpacityProperty, Anim.To(0.8, _filterMorphDuration, _filterEaseOut, FillBehavior.Stop), HandoffBehavior.SnapshotAndReplace);

			if (double.IsNaN(oldYAbs)) return;

			var container = TryGetSceneItemContainer(s);
			if (container is null) return;

			double currentLayoutY = GetSceneInnerY(s);
			if (double.IsNaN(currentLayoutY)) return;
			double from = oldYAbs - currentLayoutY;

			if (Math.Abs(from) > 0.5)
			{
				// Pre-seed local Y = from BEFORE BeginAnimation. Without this, render thread
				// can paint one frame at the layout-only position (local Y=0 → visual at newY)
				// before the animation clock ticks, causing a forward-back-forward visual dance.
				// HoldEnd holds animated value at To=0 indefinitely so the seeded local doesn't
				// resurface post-animation; SnapshotAndReplace keeps subsequent cycles clean.
				if (container.RenderTransform is not TranslateTransform tt)
				{
					tt = new TranslateTransform { Y = from };
					container.RenderTransform = tt;
				}
				else
				{
					tt.BeginAnimation(TranslateTransform.YProperty, null);
					tt.Y = from;
				}
				var slide = Anim.From(from, 0, _filterMorphDuration, _filterEaseOut);
				tt.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
			}
		}

		private double GetSceneInnerY(SceneModel s)
		{
			var container = TryGetSceneItemContainer(s);
			if (container is null) return double.NaN;
			try { return container.TransformToVisual(this).Transform(new Point(0, 0)).Y; }
			catch { return double.NaN; }
		}

		private FrameworkElement? TryGetSceneItemContainer(SceneModel s)
			=> scenesControl.ItemContainerGenerator.ContainerFromItem(s) as FrameworkElement;

		private int NextFilterGen(Guid id)
		{
			var v = (_filterAnimGen.TryGetValue(id, out var c) ? c : 0) + 1;
			_filterAnimGen[id] = v;
			return v;
		}

		private int CurrentFilterGen(Guid id) => _filterAnimGen.TryGetValue(id, out var c) ? c : 0;

		/// <summary>
		/// Locate the live tile (CompositionThumbnail) for a specific window inside
		/// a scene's container, matched by capture handle. Null if the container
		/// isn't realised or no tile owns that handle.
		/// </summary>
		private CompositionThumbnail? FindSceneThumbnail(SceneModel scene, IntPtr handle)
		{
			if (handle == IntPtr.Zero) return null;
			var container = scenesControl.ItemContainerGenerator.ContainerFromItem(scene) as DependencyObject;
			if (container is null) return null;
			foreach (var ct in EnumerateVisualDescendants<CompositionThumbnail>(container))
				if (ct.PreviewHandle == handle) return ct;
			return null;
		}

		private static System.Collections.Generic.IEnumerable<T> EnumerateVisualDescendants<T>(DependencyObject root)
			where T : DependencyObject
		{
			int count = VisualTreeHelper.GetChildrenCount(root);
			for (int i = 0; i < count; i++)
			{
				var child = VisualTreeHelper.GetChild(root, i);
				if (child is T match) yield return match;
				foreach (var nested in EnumerateVisualDescendants<T>(child))
					yield return nested;
			}
		}

		private FrameworkElement? TryGetSceneInnerGrid(SceneModel s)
		{
			var container = scenesControl.ItemContainerGenerator.ContainerFromItem(s) as FrameworkElement;
			if (container == null) return null;
			if (VisualTreeHelper.GetChildrenCount(container) == 0) return null;
			return VisualTreeHelper.GetChild(container, 0) as FrameworkElement;
		}

		/// <summary>
		/// Return every scene tile to its resting scale. Matches the shape the SceneOpacity
		/// style installs: a ScaleTransform carrying the 1.08 hover pop.
		/// <para>
		/// It only ever unwinds when the cursor leaves the tile, via the trigger's ExitActions,
		/// and holds its last value indefinitely when that never happens. Two paths take the
		/// tile out from under a resting cursor without firing it: the sidebar stowing, and a
		/// scene switch hiding and rebuilding the tiles. The tile is then left popped out.
		/// </para>
		/// </summary>
		private void ResetAllSceneHoverTransforms()
		{
			foreach (var grid in EnumerateVisualDescendants<Grid>(scenesControl))
			{
				// The style's transform is a shared frozen instance until a storyboard clones it
				// per element, so a tile that was never hovered is read-only — and already resting.
				if (grid.RenderTransform is not ScaleTransform st || st.IsFrozen) continue;
				if (st.ScaleX == 1 && st.ScaleY == 1) continue;

				// The hover storyboard holds 1.08 with FillBehavior.HoldEnd. Detaching the
				// clock first is what lets the local write below take effect.
				st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
				st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
				st.ScaleX = 1;
				st.ScaleY = 1;
			}
		}

		private void RefreshIconOverlay(double xOffset = 0)
		{
			Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
			{
				// Flush sidebar layout — Remove+Insert during selection invalidates async.
				UpdateLayout();
				var visible = Scenes.Where(s => s.IsVisible).ToList();
				_iconOverlay.UpdateIcons(visible, s => GetSceneThumbnailScreenBounds(s), GetWorkAreaBounds(), xOffset);
			});
		}

		public ObservableCollection<SceneModel> Scenes { get; } = new ObservableCollection<SceneModel>();

		public IEnumerable<SceneModel?> AllScenes => Scenes.Union(new[] { _removedCurrentScene });

		public ICommand SwitchSceneCommand { get; }

		public SceneManager SceneManager { get; private set; } = null!;

		public IntPtr Handle => _thisHandle;

		public WindowMode Mode
		{
			get => _mode;
			set
			{
				if (value == _mode)
					return;

				Log.Info("MODE", $"Sidebar mode: {_mode} → {value}");

				_mode = value;

				this.Topmost = value == WindowMode.Flyover;

				ApplyWindowMode();
			}
		}

		private void ApplyWindowMode()
		{
			var newLeft = Mode == StageManager.WindowMode.OffScreen ? ParkedLeft : DockedLeft;
			if (Left == newLeft)
				return;

			// The cursor can leave without MouseLeave ever firing when the sidebar stows,
			// which strands the hover transforms. Reset them here.
			if (Mode == StageManager.WindowMode.OffScreen)
			{
				ResetAllSceneHoverTransforms();
				morePopup.IsOpen = false;
			}

			// Drop-into-tray: snap to final state, no unstow slide. A display with a left
			// neighbour always snaps — the slide would cross into the neighbour.
			if (_suppressNextModeSlide || !_canSlide)
			{
				_suppressNextModeSlide = false;
				BeginAnimation(LeftProperty, null);
				Left = newLeft;
				UpdateLayout();
				bool onScreen = Mode != StageManager.WindowMode.OffScreen;
				_iconOverlay.Enabled = onScreen;
				if (onScreen)
				{
					var visible = Scenes.Where(s => s.IsVisible).ToList();
					_iconOverlay.UpdateIcons(visible, s => GetSceneThumbnailScreenBounds(s), GetWorkAreaBounds());
					_iconOverlay.BringToFront();
				}
				else if (!_canSlide)
				{
					// Nothing slid the icons away, so they would stay behind on an empty edge.
					_iconOverlay.Hide();
				}
				return;
			}

			var isIncoming = newLeft > Left;
			var easingMode = isIncoming ? EasingMode.EaseOut : EasingMode.EaseIn;
			var duration = TimeSpan.FromSeconds(0.5);
			var easingFunction = new PowerEase { EasingMode = easingMode };

			if (isIncoming)
			{
				// Snap to final position, force layout, position icons at correct coords, then animate
				BeginAnimation(LeftProperty, null);
				Left = DockedLeft;
				UpdateLayout();
				_iconOverlay.Enabled = true;
				var visible = Scenes.Where(s => s.IsVisible).ToList();
				_iconOverlay.UpdateIcons(visible, s => GetSceneThumbnailScreenBounds(s), GetWorkAreaBounds());
				_iconOverlay.SlideIn(-Width, duration, easingFunction);
				Left = ParkedLeft;
				// MainWindow's Topmost flip in the Mode setter pushed the
				// sidebar above the icon overlay in the topmost z-stack.
				// Re-assert overlay HWND_TOPMOST so icons render on top.
				_iconOverlay.BringToFront();
			}
			else
			{
				_iconOverlay.Enabled = false;
				_iconOverlay.SlideOut(-Width, duration, easingFunction);
			}

			// 0%-keyframe = current Left so outgoing (Left=0 → -Width) animates
			// instead of snapping. Incoming sets Left=-Width above so it still
			// picks up from the correct start.
			var animation = new DoubleAnimationUsingKeyFrames { Duration = new Duration(duration) };
			animation.KeyFrames.Add(new EasingDoubleKeyFrame(Left, KeyTime.FromPercent(0)));
			animation.KeyFrames.Add(new EasingDoubleKeyFrame(newLeft, KeyTime.FromPercent(1.0), easingFunction));

			BeginAnimation(LeftProperty, animation);
		}

		private void StartHook()
		{
			if (_hook is null)
			{
				_hook = new TaskPoolGlobalHook();
				Task.Run(_hook.Run);
			}
			_hookUsers++;

			_hook.MousePressed += OnMousePressed;
			_hook.MouseReleased += OnMouseReleased;
			_hook.MouseMoved += _hook_MouseMoved;
		}

		private void StopHook()
		{
			if (_hook is null) return;
			_hook.MousePressed -= OnMousePressed;
			_hook.MouseReleased -= OnMouseReleased;
			_hook.MouseMoved -= _hook_MouseMoved;

			if (--_hookUsers > 0) return;

			try
			{
				_hook.Dispose();
			}
			catch (HookException)
			{
			}
			_hook = null;
		}

		/// <summary>True when a hook point (physical pixels) lies on this sidebar's display.</summary>
		private bool IsOnMyMonitor(long x, long y) =>
			x >= _monitorPx.Left && x < _monitorPx.Right && y >= _monitorPx.Top && y < _monitorPx.Bottom;

		// How close to the display's left edge the pointer has to come to bring a stowed strip
		// out, physical pixels. Where that edge is a wall the pointer stops at, touching it is
		// enough, as on macOS — the old 44 px band popped the strip over the left of every
		// maximized window (back buttons, activity bars, folder panes) whenever the pointer
		// merely passed near. With a display to the left, or the taskbar there, the edge is no
		// wall, and a band is the only way to aim at it.
		private const int EdgeTouchPx = 2;
		private const int EdgeBandPx = 44;
		private int _edgeTriggerPx = EdgeBandPx;

		/// <summary>The sidebar's width in physical pixels — the space the hook coordinates live in.</summary>
		private double SidebarWidthPx => _lastWidth * _lastDpiX;

		private void _hook_MouseMoved(object? sender, MouseHookEventArgs e)
		{
			Interlocked.Exchange(ref _mouseX, e.Data.X);
			Interlocked.Exchange(ref _mouseY, e.Data.Y);

			if (Mode != WindowMode.OffScreen || !IsOnMyMonitor(e.Data.X, e.Data.Y))
				return;

			// A window being dragged into the strip should see where it is going: macOS brings
			// the strip out for it the same way.
			var draggingIn = _dragDropManager?.IsShrinkingIntoStrip == true;
			if (draggingIn || e.Data.X - _workPx.Left <= _edgeTriggerPx)
			{
				Dispatcher.Invoke(() =>
				{
					Mode = WindowMode.Flyover;
					if (draggingIn)
						BringTransitionOverlayToFront();
				});
			}
		}

		/// <summary>
		/// Raises the overlay the drag ghost flies on above the strip, which the Flyover mode
		/// just made topmost — otherwise the card slides underneath the tiles it is heading for.
		/// </summary>
		private void BringTransitionOverlayToFront()
		{
			if (_sceneTransitionAnimator.Overlay is not { } overlay) return;
			var hwnd = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
			if (hwnd == IntPtr.Zero) return;
			Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
				Win32.SetWindowPosFlags.IgnoreMove | Win32.SetWindowPosFlags.IgnoreResize | Win32.SetWindowPosFlags.DoNotActivate);
		}

		private bool _closing;
		private int _overlapCheckQueued;

		private void OverlapCheck(object? _)
		{
			// Timer thread. Everything the check reads — the scene's window list, the tracked
			// windows — is mutated on the UI thread, and enumerating it from here could throw
			// "collection was modified", which on a timer thread ends the process. So the whole
			// check runs on the UI thread; one queued at a time, so a busy dispatcher is not
			// buried under them.
			if (Interlocked.Exchange(ref _overlapCheckQueued, 1) == 1) return;
			Dispatcher.BeginInvoke(() =>
			{
				Volatile.Write(ref _overlapCheckQueued, 0);

				// The timer starts before OnContentRendered builds the SceneManager.
				if (_closing || SceneManager is null) return;

				// Don't hide the sidebar while dragging a window toward it
				if (_dragDropManager?.IsDragging == true) return;

				UpdateModeByWindows(SceneManager.GetCurrentWindows().ToArray());
				SceneManager.ReconcileForeground();
			});
		}

		private void UpdateModeByWindows(IEnumerable<IWindow> windows)
		{
			// Freeze sidebar mode in desktop view: scene windows are stowed (alpha=0) but still
			// report layout positions, so doesOverlap() would falsely flag overlap and hide the tray.
			// With "Show recent apps" off the strip stays stowed everywhere, desktop view included.
			if (SceneManager.IsDesktopView && _showRecentApps)
				return;

			// The More list hangs off the strip; stowing it would pull the list's anchor away.
			if (_moreListOpen)
				return;

			// Window rects and hook points are physical pixels; the sidebar width is DIPs. Compared
			// unscaled, at 150 % the strip counted as 240 px wide instead of 360 — a Flyover strip
			// slid away while the pointer was still over the right half of its tiles.
			var sidebarWidthPx = SidebarWidthPx;
			bool doesOverlap(IWindowLocation loc) => loc.State == Native.Window.WindowState.Maximized || (loc.State == Native.Window.WindowState.Normal && loc.X - _workPx.Left < sidebarWidthPx * STOW_OVERLAP_FRACTION);

			var anyOverlappingWindows = !_showRecentApps || windows.Any(w => doesOverlap(w.Location));

			var mouseX = Interlocked.Read(ref _mouseX);
			var mouseY = Interlocked.Read(ref _mouseY);
			var containsMouse = IsOnMyMonitor(mouseX, mouseY) && mouseX - _workPx.Left <= sidebarWidthPx;
			var setMode = Mode == WindowMode.OnScreen && !containsMouse
							|| Mode == WindowMode.OffScreen
							|| (Mode == WindowMode.Flyover && !containsMouse);

			if (setMode)
			{
				Dispatcher.Invoke(() =>
				{
					Mode = anyOverlappingWindows ? WindowMode.OffScreen : WindowMode.OnScreen;
				});
			}
		}

		/// <summary>
		/// Outlines the sidebar (blue) and drag buffer (yellow) zones. Debug builds only, and
		/// only on request (<c>STAGEMANAGER_DEBUG_ZONES=1</c>) — `dotnet run` builds Debug, so
		/// showing it unconditionally put tinted boxes over every developer's screen.
		/// </summary>
		[System.Diagnostics.Conditional("DEBUG")]
		private void ShowDebugDragZones()
		{
			if (Environment.GetEnvironmentVariable("STAGEMANAGER_DEBUG_ZONES") != "1")
				return;

			var sidebarW = _lastWidth;
			var bufferW = DragDropManager.BufferWidthLogical;
			var workArea = GetWorkAreaBounds();
			_debugZoneOverlay.Show(
				new Rect(0, 0, sidebarW, workArea.Height),
				new Rect(sidebarW, 0, bufferW, workArea.Height),
				workArea);
		}

		/// <summary>
		/// DPI scale factors for converting between physical and logical coordinates.
		/// Cached lazily; invalidated on <see cref="OnDpiChanged"/>.
		/// </summary>
		private Point _dpi = new(1.0, 1.0);
		private bool _dpiCached;

		// Snapshot of Dpi.X for the SharpHook callbacks. They run off the UI thread and
		// hook coordinates are physical, so anything they compare against a WPF DIP width
		// has to be scaled — see OnMouseReleased.
		private double _lastDpiX = 1.0;

		private Point Dpi
		{
			get
			{
				if (_dpiCached) return _dpi;
				var source = PresentationSource.FromVisual(this);
				if (source?.CompositionTarget == null)
					return _dpi; // pre-source-init: return fallback but don't cache it
				_dpi = new Point(source.CompositionTarget.TransformToDevice.M11, source.CompositionTarget.TransformToDevice.M22);
				_dpiCached = true;
				_lastDpiX = _dpi.X;
				return _dpi;
			}
		}

		protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
		{
			base.OnDpiChanged(oldDpi, newDpi);
			_dpiCached = false;
		}

		/// <summary>
		/// Returns the screen bounds of a scene's thumbnail in WPF logical (DPI-independent) units.
		/// </summary>
		private Rect GetSceneThumbnailScreenBounds(SceneModel sceneModel)
		{
			try
			{
				var container = scenesControl.ItemContainerGenerator.ContainerFromItem(sceneModel) as FrameworkElement;
				if (container == null)
					return Rect.Empty;

				var dpi = Dpi;

				// Get screen coordinates (physical pixels) then convert to logical units
				var topLeft = container.TranslatePoint(new Point(0, 0), this);
				var bottomRight = container.TranslatePoint(new Point(container.ActualWidth, container.ActualHeight), this);

				var screenTopLeft = PointToScreen(topLeft);
				var screenBottomRight = PointToScreen(bottomRight);

				return new Rect(
					screenTopLeft.X / dpi.X,
					screenTopLeft.Y / dpi.Y,
					(screenBottomRight.X - screenTopLeft.X) / dpi.X,
					(screenBottomRight.Y - screenTopLeft.Y) / dpi.Y);
			}
			catch
			{
				return Rect.Empty;
			}
		}

		/// <summary>Screen bounds of any sidebar element in WPF logical units, or Rect.Empty.</summary>
		private Rect GetElementScreenBounds(FrameworkElement element)
		{
			try
			{
				if (!element.IsVisible) return Rect.Empty;
				var dpi = Dpi;
				var tl = element.PointToScreen(new Point(0, 0));
				var br = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
				return new Rect(tl.X / dpi.X, tl.Y / dpi.Y, (br.X - tl.X) / dpi.X, (br.Y - tl.Y) / dpi.Y);
			}
			catch
			{
				return Rect.Empty;
			}
		}

		/// <summary>
		/// Converts a window's Location (physical pixels) to WPF logical units.
		/// Returns Rect.Empty if the window is minimized, offscreen-parked, or invalid.
		/// </summary>
		private Rect WindowToLogicalRect(Native.Window.IWindow? window)
		{
			if (window == null || window.IsMinimized)
				return Rect.Empty;

			var loc = window.Location;
			if (loc.Width <= 0 || loc.Height <= 0)
				return Rect.Empty;

			// If OpacityWindowStrategy has parked this window off-screen, use the saved
			// original position so the animator targets the on-screen rect the window will
			// occupy after Show — not its current parked location.
			int x = loc.X, y = loc.Y;
			if (Strategies.OpacityWindowStrategy.TryGetOriginalPosition(window.Handle, out var ox, out var oy))
			{
				x = ox; y = oy;
			}
			else if (loc.X < -10000)
			{
				return Rect.Empty;
			}

			var dpi = Dpi;
			return new Rect(x / dpi.X, y / dpi.Y, loc.Width / dpi.X, loc.Height / dpi.Y);
		}

		private Rect GetSceneWindowBounds(SceneModel sceneModel)
		{
			var window = sceneModel.Scene.Windows.FirstOrDefault(w => !w.IsMinimized);
			var rect = WindowToLogicalRect(window);
			return rect != Rect.Empty ? rect : GetWorkAreaBounds();
		}

		private Rect GetCurrentSceneWindowBounds()
		{
			var window = SceneManager.GetCurrentWindows().FirstOrDefault(w => !w.IsMinimized);
			return WindowToLogicalRect(window);
		}

		/// <summary>
		/// Returns this sidebar's display work area in WPF logical (DPI-independent) units.
		/// Taken from the assigned display rather than MonitorFromWindow: while the sidebar is
		/// parked it sits beside, or even on, a different display.
		/// </summary>
		private Rect GetWorkAreaBounds()
		{
			if (PresentationSource.FromVisual(this) is not System.Windows.Interop.HwndSource)
				return Rect.Empty;

			var dpi = Dpi;
			return new Rect(
				_workPx.Left / dpi.X,
				_workPx.Top / dpi.Y,
				_workPx.Width / dpi.X,
				_workPx.Height / dpi.Y);
		}

		#region "More" button

		// Read from the overlap timer thread, so not the Popup's own IsOpen.
		private volatile bool _moreListOpen;
		private int _moreCount;

		/// <summary>Hidden (not collapsed) when empty: the strip keeps its layout, so tiles never jump.</summary>
		public Visibility MoreButtonVisibility => _moreCount > 0 ? Visibility.Visible : Visibility.Hidden;

		public string MoreButtonText => $"{_moreCount} more";

		/// <summary>Scenes waiting beyond the strip's cap, most recently used first.</summary>
		private SceneModel[] GetOverflowScenes() => _filterProcessKey != null
			? Array.Empty<SceneModel>()
			: Scenes.Where(s => !s.IsVisible && !s.IsHiddenButReserved).OrderByDescending(s => s.Updated).ToArray();

		private void UpdateMoreButton()
		{
			var count = GetOverflowScenes().Length;
			if (count == _moreCount) return;
			_moreCount = count;
			RaisePropertyChanged(nameof(MoreButtonVisibility));
			RaisePropertyChanged(nameof(MoreButtonText));
			if (count == 0)
				morePopup.IsOpen = false;
		}

		private void MoreButton_Click(object sender, RoutedEventArgs e)
		{
			if (morePopup.IsOpen)
			{
				morePopup.IsOpen = false;
				return;
			}
			var items = GetOverflowScenes().Select(s => new MoreItem(s)).ToArray();
			moreList.ItemsSource = items;
			morePopup.IsOpen = true;

			// Snapshots off the UI thread, each dropped in as soon as it is ready: the list
			// opens at once with icons and fills in, rather than waiting on the slowest window.
			foreach (var item in items)
			{
				var hwnd = item.Scene.Windows.FirstOrDefault()?.Handle ?? IntPtr.Zero;
				Task.Run(() => Helpers.WindowSnapshot.Capture(hwnd, 224))
					.ContinueWith(t =>
					{
						if (t.Result is { } preview)
							item.Preview = preview;
					}, TaskScheduler.FromCurrentSynchronizationContext());
			}
		}

		private async void MoreItem_Click(object sender, RoutedEventArgs e)
		{
			if (sender is not FrameworkElement { Tag: MoreItem { Scene: var scene } }) return;
			morePopup.IsOpen = false;
			if (_filterProcessKey != null)
				ClearAppFilter();
			await AnimatedSwitchTo(scene.Scene);
		}

		private void MorePopup_Opened(object? sender, EventArgs e) => _moreListOpen = true;

		private void MorePopup_Closed(object? sender, EventArgs e) => _moreListOpen = false;

		#endregion

		/// <summary>
		/// Starts sliding the sidebar off, the first half of turning the stage off. The overlap
		/// timer is stopped so it cannot bring the strip back while the host waits out the slide.
		/// </summary>
		internal void BeginStow()
		{
			_overlapCheckTimer?.Change(Timeout.Infinite, Timeout.Infinite);
			_filterProcessKey = null;
			Mode = WindowMode.OffScreen;
		}

		/// <summary>
		/// Closes the sidebar and has its SceneManager put this display's windows back.
		/// <paramref name="minimizeHidden"/> false leaves every window on screen where it was.
		/// </summary>
		internal void CloseAndRestoreWindows(bool minimizeHidden)
		{
			if (SceneManager is not null)
				SceneManager.MinimizeOnStop = minimizeHidden;
			Close();
		}

		/// <summary>Applies a change made in the settings window to this sidebar.</summary>
		private void OnSettingChanged(string name)
		{
			switch (name)
			{
				case "RecentAppsCount":
					_maxScenes = Settings.GetRecentAppsCount();
					AnimateSyncVisibility();
					break;
				case "ShowRecentApps":
					_showRecentApps = Settings.GetShowRecentApps();
					if (SceneManager is not null)
					{
						if (_showRecentApps && SceneManager.IsDesktopView)
							Mode = WindowMode.OnScreen;
						else
							UpdateModeByWindows(SceneManager.GetCurrentWindows().ToArray());
					}
					break;
				case "HideDesktopIcons":
					_hideDesktopIcons = Settings.GetHideDesktopIcons();
					if (SceneManager is not null)
						SceneManager.HideDesktopIconsEnabled = _hideDesktopIcons;
					break;
			}
		}

		private void RaisePropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string memberName = "")
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(memberName));
		}
	}

	public enum WindowMode
	{
		OnScreen,
		OffScreen,
		Flyover
	}
}
