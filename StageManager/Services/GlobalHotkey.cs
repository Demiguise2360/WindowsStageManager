using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace StageManager.Services
{
	/// <summary>
	/// One system-wide shortcut via RegisterHotKey.
	/// <para>
	/// Registered without a window: WM_HOTKEY then goes to the UI thread's queue as a thread
	/// message, and WPF surfaces those through <see cref="ComponentDispatcher.ThreadFilterMessage"/>.
	/// That keeps the shortcut alive while the stage is off and no sidebar window exists.
	/// Construct and dispose on the UI thread — the registration belongs to the calling thread.
	/// </para>
	/// </summary>
	internal sealed class GlobalHotkey : IDisposable
	{
		private const int WM_HOTKEY = 0x0312;
		private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

		private static int s_nextId = 0x5A00;

		private readonly int _id;
		private bool _registered;

		public event Action? Pressed;

		public HotkeyGesture Gesture { get; }

		/// <summary>False when another app already owns the combination.</summary>
		public bool IsRegistered => _registered;

		public GlobalHotkey(HotkeyGesture gesture)
		{
			Gesture = gesture;
			_id = s_nextId++;

			uint mods = MOD_NOREPEAT;
			if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= MOD_ALT;
			if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) mods |= MOD_CONTROL;
			if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= MOD_SHIFT;
			if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= MOD_WIN;

			var vk = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
			_registered = RegisterHotKey(IntPtr.Zero, _id, mods, vk);
			if (_registered)
				ComponentDispatcher.ThreadFilterMessage += OnThreadMessage;

			Log.Info("HOTKEY", $"Register '{gesture}' → {(_registered ? "ok" : $"failed (0x{Marshal.GetLastWin32Error():X})")}");
		}

		private void OnThreadMessage(ref MSG msg, ref bool handled)
		{
			if (handled || msg.message != WM_HOTKEY || (int)msg.wParam != _id)
				return;

			handled = true;
			Pressed?.Invoke();
		}

		public void Dispose()
		{
			if (!_registered) return;
			_registered = false;
			ComponentDispatcher.ThreadFilterMessage -= OnThreadMessage;
			UnregisterHotKey(IntPtr.Zero, _id);
		}

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
	}
}
