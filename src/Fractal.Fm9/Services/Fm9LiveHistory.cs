using System.Text.Json;

namespace Fractal.Fm9
{
	// One word a live change wrote: enough to write it back exactly.
	public sealed record Fm9LiveWord(string BlockType, int EffectId, int Id, int Before, int After);

	// One live change, and how to undo it: write the words back, switch the channel or bypass back, or (after a model or
	// type change, which reloads voicing) restore the edit buffer from the snapshot taken just before.
	public sealed record Fm9LiveStep(
		DateTimeOffset At,
		string Description,
		IReadOnlyList<Fm9LiveWord> Words,
		int? EffectId = null,
		int? ChannelBefore = null,
		int? ChannelAfter = null,
		bool? BypassBefore = null,
		bool? BypassAfter = null,
		string? Snapshot = null);

	public sealed record Fm9LiveSnapshot(string Name, int Preset, string PresetName, DateTimeOffset At, string File);

	public sealed record Fm9LiveHistoryState(int Preset, string PresetName, List<Fm9LiveStep> Steps, List<Fm9LiveSnapshot> Snapshots);

	// The live edits made to the preset showing, newest last, kept in the work folder so undo survives a restart. It starts
	// again when another preset is showing or the edits are stored; named snapshots stay until replaced.
	public sealed class Fm9LiveHistory
	{
		private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
		private readonly string _folder;

		public Fm9LiveHistory(string stateFolder) => _folder = stateFolder ?? throw new ArgumentNullException(nameof(stateFolder));

		private string FilePath => Path.Combine(_folder, "live-history.json");
		public string SnapshotFolder => Path.Combine(_folder, "snapshots");

		public Fm9LiveHistoryState Load()
		{
			if (!File.Exists(FilePath)) return new(-1, "", [], []);
			return JsonSerializer.Deserialize<Fm9LiveHistoryState>(File.ReadAllBytes(FilePath), Json) ?? new(-1, "", [], []);
		}

		public void Save(Fm9LiveHistoryState state)
		{
			Directory.CreateDirectory(_folder);
			Fm9Files.WriteAtomically(FilePath, JsonSerializer.SerializeToUtf8Bytes(state, Json));
		}

		// The history for the preset showing: a different preset (or a renamed one) starts it again.
		public Fm9LiveHistoryState For(int preset, string presetName)
		{
			Fm9LiveHistoryState state = Load();
			if (state.Preset == preset && state.PresetName == presetName) return state;
			DeleteStepSnapshots(state);
			return state with { Preset = preset, PresetName = presetName, Steps = [] };
		}

		// After a store, an audition or a preset change the steps no longer describe the edit buffer.
		public void Reset()
		{
			Fm9LiveHistoryState state = Load();
			DeleteStepSnapshots(state);
			Save(state with { Preset = -1, PresetName = "", Steps = [] });
		}

		public string NewSnapshotPath(string name)
		{
			Directory.CreateDirectory(SnapshotFolder);
			return Path.Combine(SnapshotFolder, $"{Fm9Device.FileName(name)}.syx");
		}

		private static void DeleteStepSnapshots(Fm9LiveHistoryState state)
		{
			foreach (Fm9LiveStep step in state.Steps)
				if (step.Snapshot is { } file && File.Exists(file) && !state.Snapshots.Any(snapshot => snapshot.File == file))
					File.Delete(file);
		}
	}
}
