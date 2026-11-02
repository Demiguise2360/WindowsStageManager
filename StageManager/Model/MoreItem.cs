using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace StageManager.Model
{
	/// <summary>
	/// One row of the sidebar's "More" list. The preview arrives later — snapshots are taken
	/// in the background after the list opens — so the row starts with the app icon alone.
	/// </summary>
	public sealed class MoreItem : INotifyPropertyChanged
	{
		private ImageSource? _preview;

		public MoreItem(SceneModel scene)
		{
			Scene = scene;
		}

		public SceneModel Scene { get; }

		public string Title => Scene.Title;

		public ImageSource? Icon => Scene.Icon;

		public ImageSource? Preview
		{
			get => _preview;
			set
			{
				_preview = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlaceholderVisibility)));
			}
		}

		/// <summary>The big icon shown in the preview box until (or unless) a snapshot exists.</summary>
		public Visibility PlaceholderVisibility => _preview is null ? Visibility.Visible : Visibility.Collapsed;

		public event PropertyChangedEventHandler? PropertyChanged;
	}
}
