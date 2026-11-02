using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace StageManager.Services
{
	/// <summary>
	/// A modifier + key combination for a global shortcut, stored as text ("Ctrl+Alt+S").
	/// At least one modifier is required — a bare key would fire while typing.
	/// </summary>
	public sealed record HotkeyGesture(ModifierKeys Modifiers, Key Key)
	{
		public static HotkeyGesture Default { get; } = new(ModifierKeys.Control | ModifierKeys.Alt, Key.S);

		public bool IsValid => Modifiers != ModifierKeys.None && !IsModifierKey(Key) && Key != Key.None;

		public static bool IsModifierKey(Key key) => key is
			Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
			Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

		public override string ToString()
		{
			var parts = new List<string>();
			if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
			if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
			if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
			if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
			parts.Add(KeyName(Key));
			return string.Join("+", parts);
		}

		/// <summary>Human-readable form for menus and the settings window.</summary>
		public string DisplayText => ToString().Replace("+", " + ");

		public static bool TryParse(string text, out HotkeyGesture? gesture)
		{
			gesture = null;
			if (string.IsNullOrWhiteSpace(text))
				return false;

			var modifiers = ModifierKeys.None;
			Key? key = null;
			foreach (var raw in text.Split('+').Select(p => p.Trim()))
			{
				switch (raw.ToLowerInvariant())
				{
					case "ctrl": modifiers |= ModifierKeys.Control; break;
					case "alt": modifiers |= ModifierKeys.Alt; break;
					case "shift": modifiers |= ModifierKeys.Shift; break;
					case "win": modifiers |= ModifierKeys.Windows; break;
					default:
						if (raw.Length == 1 && char.IsDigit(raw[0]))
							key = Key.D0 + (raw[0] - '0');
						else if (Enum.TryParse<Key>(raw, true, out var k))
							key = k;
						else
							return false;
						break;
				}
			}

			if (key is null)
				return false;
			gesture = new HotkeyGesture(modifiers, key.Value);
			return gesture.IsValid;
		}

		private static string KeyName(Key key) => key switch
		{
			>= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
			_ => key.ToString(),
		};
	}
}
