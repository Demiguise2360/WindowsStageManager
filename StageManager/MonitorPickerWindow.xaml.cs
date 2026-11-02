using StageManager.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Screen = System.Windows.Forms.Screen;

namespace StageManager
{
	/// <summary>
	/// Startup choice of the displays that run a stage: all of them, or exactly one.
	/// While it is open every display shows its number, so "Display 2" in the list can be
	/// matched to a physical screen.
	/// </summary>
	public partial class MonitorPickerWindow : Window
	{
		internal sealed class Option : INotifyPropertyChanged
		{
			private bool _isSelected;

			public required string Selection { get; init; }
			public required string Title { get; init; }
			public required string Detail { get; init; }

			public bool IsSelected
			{
				get => _isSelected;
				set
				{
					if (_isSelected == value) return;
					_isSelected = value;
					PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
				}
			}

			public event PropertyChangedEventHandler? PropertyChanged;
		}

		private readonly List<Option> _options = new();
		private readonly List<Window> _identifiers = new();

		/// <summary>
		/// <see cref="Settings.AllMonitors"/> or a display device name. Set when the dialog
		/// returns true.
		/// </summary>
		public string Selection { get; private set; } = Settings.AllMonitors;

		public bool Remember => rememberCheckBox.IsChecked == true;

		public MonitorPickerWindow(string preselected)
		{
			InitializeComponent();

			var screens = MonitorLayout.Ordered();

			_options.Add(new Option
			{
				Selection = Settings.AllMonitors,
				Title = "All displays",
				Detail = $"A separate stage on each of the {screens.Length} displays",
			});

			foreach (var screen in screens)
			{
				_options.Add(new Option
				{
					Selection = screen.DeviceName,
					Title = $"Only display {MonitorLayout.Number(screen)}",
					Detail = MonitorLayout.Describe(screen) + " — other displays are left alone",
				});
			}

			var initial = _options.FirstOrDefault(o => string.Equals(o.Selection, preselected, StringComparison.OrdinalIgnoreCase))
				?? _options[0];
			initial.IsSelected = true;

			optionsList.ItemsSource = _options;

			Loaded += (_, _) => ShowIdentifiers();
			Closed += (_, _) => HideIdentifiers();
		}

		private void Start_Click(object sender, RoutedEventArgs e)
		{
			Selection = (_options.FirstOrDefault(o => o.IsSelected) ?? _options[0]).Selection;
			DialogResult = true;
		}

		private void Quit_Click(object sender, RoutedEventArgs e)
		{
			DialogResult = false;
		}

		private void Option_DoubleClick(object sender, MouseButtonEventArgs e)
		{
			if (sender is RadioButton { DataContext: Option option })
			{
				option.IsSelected = true;
				Start_Click(sender, e);
			}
		}

		private void ShowIdentifiers()
		{
			_identifiers.AddRange(MonitorLayout.ShowNumberBadges());

			// The badges were shown after this window and would otherwise take the top spot.
			Activate();
		}

		private void HideIdentifiers()
		{
			foreach (var badge in _identifiers)
				badge.Close();
			_identifiers.Clear();
		}
	}
}
