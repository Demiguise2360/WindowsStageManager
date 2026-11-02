using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StageManager.Services
{
	public static class SceneSnapshot
	{
		/// <param name="ProcessIds">
		/// Parallel to <paramref name="Handles"/>. A handle is only a number Windows hands out
		/// again once its window is gone, so a restore checks the process still matches — a
		/// session file outlives a reboot. Null in snapshots written before it existed.
		/// </param>
		public record SceneEntry(string Key, long[] Handles, int[]? ProcessIds = null);

		public record Snapshot(long[] ActiveSceneHandles, SceneEntry[] Scenes);

		private static readonly string SnapshotDir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"StageManager", "updates");

		private static readonly JsonSerializerOptions JsonOptions = new()
		{
			WriteIndented = false,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
		};

		/// <summary>
		/// Serializes the scene layout to a JSON file. Returns the file path.
		/// </summary>
		public static string Save(Snapshot snapshot)
		{
			Directory.CreateDirectory(SnapshotDir);
			var path = Path.Combine(SnapshotDir, "scene-snapshot.json");
			var json = JsonSerializer.Serialize(snapshot, JsonOptions);
			File.WriteAllText(path, json);
			return path;
		}

		// Every per-display SceneManager restores from the same file, but the file is deleted on
		// first read. The parsed result is kept so the later readers get the same snapshot.
		private static string? _loadedPath;
		private static Snapshot? _loaded;

		/// <summary>
		/// Loads a snapshot from the given path and deletes the file (consumed once; repeated
		/// calls for the same path return the cached result).
		/// Returns null if the file doesn't exist or is corrupt.
		/// </summary>
		public static Snapshot? Load(string path)
		{
			if (string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase))
				return _loaded;

			try
			{
				if (!File.Exists(path))
					return null;

				var json = File.ReadAllText(path);
				var snapshot = JsonSerializer.Deserialize<Snapshot>(json, JsonOptions);

				File.Delete(path);
				_loadedPath = path;
				_loaded = snapshot;
				return snapshot;
			}
			catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
			{
				return null;
			}
		}

		/// <summary>
		/// Where the layout is kept between runs: written when Stage Manager quits, read once by
		/// the next start. Unlike the update snapshot it is not handed over on the command line.
		/// </summary>
		public static readonly string SessionPath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"StageManager", "session.json");

		/// <summary>Saves the layout for the next start. Never throws — quitting must not fail over it.</summary>
		public static void SaveSession(Snapshot snapshot)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
				File.WriteAllText(SessionPath, JsonSerializer.Serialize(snapshot, JsonOptions));
				Log.Info("SESSION", $"Saved {snapshot.Scenes.Length} scenes to {SessionPath}");
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Log.Info("SESSION", $"Saving the session failed: {ex.Message}");
			}
		}

		/// <summary>
		/// A layout handed from a stage being torn down to the one built right after it, in
		/// memory — for rebuilds within one run (a display attached or removed), where the user's
		/// groupings should survive but no restart is involved. Every per-display SceneManager of
		/// the new stage restores from it; the host clears it when that stage is stopped.
		/// </summary>
		public static Snapshot? CarryOver { get; set; }

		/// <summary>
		/// Merges the snapshots of several per-display SceneManagers into one file-sized record.
		/// Each restoring SceneManager keeps only the windows on its own display, so no display
		/// tag is needed.
		/// </summary>
		public static Snapshot Combine(IEnumerable<Snapshot> parts)
		{
			var list = parts.ToArray();
			return new Snapshot(
				list.SelectMany(p => p.ActiveSceneHandles).ToArray(),
				list.SelectMany(p => p.Scenes).ToArray());
		}
	}
}
