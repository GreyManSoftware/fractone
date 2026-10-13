using Fractal.Companion.Protocol;

namespace Fractal.Fm9
{
	public sealed record Fm9DiffChannel(string Block, string Channel, IReadOnlyList<string> Changes);

	public sealed record Fm9DiffResult(
		string Before,
		string After,
		IReadOnlyList<string> Preset,
		IReadOnlyList<string> Grid,
		IReadOnlyList<Fm9DiffChannel> Blocks,
		IReadOnlyList<string> Scenes,
		IReadOnlyList<string> Modifiers)
	{
		public bool Same => Preset.Count + Grid.Count + Blocks.Count + Scenes.Count + Modifiers.Count == 0;

		// One line per change, for a quick summary ("Amp 1 A: Bass 1.75 -> 1.5, Fat ON -> OFF").
		public IReadOnlyList<string> Lines() =>
			[.. Preset, .. Grid, .. Blocks.Select(block => $"{block.Block} {block.Channel}: {string.Join(", ", block.Changes)}"), .. Scenes, .. Modifiers];
	}

	// What differs between two presets, as a player reads it: settings by block and channel in display units, scenes
	// (channel played, bypass, names), the grid (blocks added, removed, moved) and modifiers.
	public static class Fm9Diff
	{
		// soundOnly hides what cannot change the sound: names, and channels no scene plays in either preset.
		public static Fm9DiffResult Compare(Fm9UploadedPreset before, Fm9UploadedPreset after, bool soundOnly = false,
			string beforeLabel = "before", string afterLabel = "after")
		{
			ArgumentNullException.ThrowIfNull(before);
			ArgumentNullException.ThrowIfNull(after);
			List<string> preset = [];
			if (!soundOnly && before.Name != after.Name) preset.Add($"Name: '{before.Name}' -> '{after.Name}'");

			List<string> grid = [];
			foreach (Fm9UploadedBlock block in before.Blocks)
			{
				Fm9UploadedBlock? moved = after.Find(block.BlockType, block.Instance);
				if (moved is null) grid.Add($"Removed {Fm9Presets.Name(block)} (row {block.Row}, column {block.Column})");
				else if ((moved.Row, moved.Column) != (block.Row, block.Column))
					grid.Add($"Moved {Fm9Presets.Name(block)}: row {block.Row}, column {block.Column} -> row {moved.Row}, column {moved.Column}");
			}
			foreach (Fm9UploadedBlock block in after.Blocks.Where(block => before.Find(block.BlockType, block.Instance) is null))
				grid.Add($"Added {Fm9Presets.Name(block)} (row {block.Row}, column {block.Column})");

			List<Fm9DiffChannel> blocks = [];
			List<string> scenes = [];
			for (int scene = 1; scene <= Fm9Presets.SceneCount; scene++)
			{
				string oldName = before.GetSceneName(scene), newName = after.GetSceneName(scene);
				if (!soundOnly && oldName != newName) scenes.Add($"Scene {scene} name: '{oldName}' -> '{newName}'");
			}
			foreach (Fm9UploadedBlock old in before.Blocks)
			{
				if (after.Find(old.BlockType, old.Instance) is not { } current) continue;
				string name = Fm9Presets.Name(old);
				if (!old.IsEditable || !current.IsEditable)
				{
					if (!RawSame(before, old, after, current)) blocks.Add(new(name, "", ["changed (this block's settings cannot be read here)"]));
					continue;
				}
				HashSet<int> played = soundOnly ? Played(before, old).Concat(Played(after, current)).ToHashSet() : [];
				int channels = Math.Min(Fm9UploadedPreset.ChannelsOf(old), Fm9UploadedPreset.ChannelsOf(current));
				for (int channel = 0; channel < channels; channel++)
				{
					if (soundOnly && !played.Contains(channel)) continue;
					IReadOnlyDictionary<string, string> a = before.GetParameters(old, channel), b = after.GetParameters(current, channel);
					List<string> changes = [];
					foreach (string setting in a.Keys.Union(b.Keys).Order(StringComparer.Ordinal))
					{
						string? x = a.GetValueOrDefault(setting), y = b.GetValueOrDefault(setting);
						if (x == y) continue;
						changes.Add($"{setting} {Fm9Names.Display(old.BlockType, setting, x) ?? "(none)"} -> {Fm9Names.Display(old.BlockType, setting, y) ?? "(none)"}");
					}
					if (changes.Count > 0) blocks.Add(new(name, Fm9Names.Letter(channel), changes));
				}
				if (old.BlockType is "Input" or "Output") continue;
				for (int scene = 1; scene <= Fm9Presets.SceneCount; scene++)
				{
					Fm9PresetBlockSceneState x = before.GetSceneState(old, scene), y = after.GetSceneState(current, scene);
					if (x.Channel != y.Channel) scenes.Add($"Scene {scene}: {name} plays {Fm9Names.Letter((int)x.Channel)} -> {Fm9Names.Letter((int)y.Channel)}");
					if (x.Bypassed != y.Bypassed) scenes.Add($"Scene {scene}: {name} {(y.Bypassed ? "bypassed" : "engaged")} (was {(x.Bypassed ? "bypassed" : "engaged")})");
				}
			}

			List<string> modifiers = [];
			string[] oldModifiers = Describe(before), newModifiers = Describe(after);
			modifiers.AddRange(oldModifiers.Except(newModifiers).Select(modifier => $"Modifier removed: {modifier}"));
			modifiers.AddRange(newModifiers.Except(oldModifiers).Select(modifier => $"Modifier added: {modifier}"));
			return new(beforeLabel, afterLabel, preset, grid, blocks, scenes, modifiers);
		}

		private static IEnumerable<int> Played(Fm9UploadedPreset preset, Fm9UploadedBlock block) =>
			Enumerable.Range(1, Fm9Presets.SceneCount).Select(scene => preset.GetSceneState(block, scene))
				.Where(state => !state.Bypassed || block.BlockType is "Input" or "Output").Select(state => (int)state.Channel);

		private static bool RawSame(Fm9UploadedPreset a, Fm9UploadedBlock x, Fm9UploadedPreset b, Fm9UploadedBlock y) =>
			x.Record is not null && y.Record is not null &&
			a.Body.Bytes.Span.Slice(x.Record.Offset, x.Record.Length).SequenceEqual(b.Body.Bytes.Span.Slice(y.Record.Offset, y.Record.Length));

		private static string[] Describe(Fm9UploadedPreset preset) =>
			Fm9PresetModifiers.Read(preset.Body).Select(entry => entry.Modifier)
				.Select(modifier => $"{modifier.Source} -> {modifier.BlockType} {modifier.Instance} {modifier.Parameter} ({modifier.Start}-{modifier.End}%, channel {modifier.Channel})")
				.ToArray();
	}
}
