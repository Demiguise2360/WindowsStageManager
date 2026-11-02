using StageManager.Native.Window;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace StageManager.Model
{
    [System.Diagnostics.DebuggerDisplay("{Title}")]
	public class Scene
	{
		public event EventHandler? SelectedChanged;

		public Guid Id { get; } = Guid.NewGuid();

		/// <summary>
		/// The member windows' titles, read live. Cached once, it went stale the moment a window
		/// retitled itself (a browser switching tabs, an editor opening a file), and the "More"
		/// list kept naming what the window showed back when it joined.
		/// </summary>
		public string Title => string.Join(Environment.NewLine, _windows.ToArray().Select(w => Max(20, w.Title)));

		public IEnumerable<IWindow> Windows => _windows;

		private List<IWindow> _windows = new List<IWindow>();
		private bool _selected;

		public Scene(string key, params IWindow[] windows)
		{
			_windows.AddRange(windows);
			Key = key;
		}

		public void Remove(IWindow window)
		{
			_windows.Remove(window);
		}

		public void Add(IWindow window)
		{
			_windows.Add(window);
		}

		private string Max(int max, string title)
		{
			if (title.Length > max)
				return title.Substring(0, max - 3) + "...";

			return title;
		}

		public bool IsSelected
		{
			get => _selected;
			set 
			{
				if (_selected != value)
				{
					_selected = value;
					SelectedChanged?.Invoke(this, EventArgs.Empty);
				}
			}
		}

		public string Key { get; }
	}
}
