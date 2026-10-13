using System.Globalization;
using System.Text.RegularExpressions;

namespace Fractal.Fm9
{
	// Measured with the Output block's trims and master in force: the scene's own channel's values.
	public sealed record Fm9SceneLevelState(int Scene, double Lufs, double PeakDecibels, double Trim, double Master);

	public sealed record Fm9PresetLevelPlan(IReadOnlyList<Fm9LevelPlan> Scenes, double MasterChange, double? PeakTarget,
		double PredictedLoudestPeak, IReadOnlyList<string> Notes);

	// Levels a whole preset in one pass with the Output block, which comes after every other block: the scenes are balanced
	// against each other by loudness with their "Scene N Level" trims, and the preset as a whole is set with the master
	// "Level" so its loudest scene peaks at a target, the way players gain-stage to the output meters. The master takes
	// the part of the move every scene shares, so the trims stay near 0 dB and only carry the differences.
	public static class Fm9PresetLevelling
	{
		public const string MasterSetting = "Level";
		public const double MinimumMaster = -80, MaximumMaster = 20;
		// Peaks about a quarter into the red of the FM9's output meters. Fractal calibrates the meters with 12 dB of
		// headroom at the red line, so the red line is taken as -12 dBFS on Output 1 (and its USB copy) and the target as
		// 3 dB above it. An assumption until checked against the meters on the unit itself.
		public const double DefaultPeakTarget = -9;

		// target balances the scenes: "match N" (scene N's loudness, default the first scene measured) or a loudness such
		// as "-18" LUFS. peakTarget (dBFS) then sets the preset's level so the loudest scene peaks there; with no peak
		// target, an absolute loudness sets it instead, and "match N" leaves the master alone. The master moves every scene,
		// so it is used only when every scene was measured; levelling some scenes uses their own trims and nothing else.
		public static Fm9PresetLevelPlan Plan(IReadOnlyList<Fm9SceneLevelState> measured, string? target,
			IReadOnlyDictionary<int, double>? offsets, double? peakTarget)
		{
			bool wholePreset = Enumerable.Range(1, 8).All(scene => measured.Any(level => level.Scene == scene));
			ArgumentNullException.ThrowIfNull(measured);
			if (measured.Count == 0) throw new ArgumentException("Measure at least one scene first.", nameof(measured));
			foreach (Fm9SceneLevelState level in measured)
			{
				if (level.Scene is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(measured), "Scenes are 1 to 8.");
				if (!double.IsFinite(level.Lufs)) throw new ArgumentException($"Scene {level.Scene}'s measurement has no loudness (was anything playing?).");
			}
			IReadOnlyList<Fm9SceneLevelState> scenes = [.. measured.OrderBy(level => level.Scene)];
			(bool absolute, double anchor) = ParseTarget(target ?? $"match {scenes[0].Scene}", scenes);

			// The change each scene needs, in dB at the output.
			Dictionary<int, double> change = scenes.ToDictionary(level => level.Scene,
				level => anchor + (offsets?.GetValueOrDefault(level.Scene) ?? 0) - level.Lufs);
			List<string> notes = [];
			if (peakTarget is double peak)
			{
				double loudest = scenes.Max(level => level.PeakDecibels + change[level.Scene]);
				foreach (int scene in change.Keys.ToList()) change[scene] += peak - loudest;
				if (absolute) notes.Add("The peak target sets the preset's level, so the loudness target only balanced the scenes.");
			}

			// The master takes the median of where the trims would land, so they stay centred on 0 dB.
			double masterChange = 0;
			if ((peakTarget is not null || absolute) && !wholePreset)
				notes.Add("Only some scenes were levelled, so each used its own trim; the master Level, which moves every scene, was left alone.");
			if ((peakTarget is not null || absolute) && wholePreset)
			{
				List<double> landing = [.. scenes.Select(level => level.Trim + change[level.Scene]).Order()];
				double middle = landing.Count % 2 == 1 ? landing[landing.Count / 2] : (landing[(landing.Count / 2) - 1] + landing[landing.Count / 2]) / 2;
				masterChange = Math.Round(middle, 1);
				double highest = scenes.Max(level => level.Master), lowest = scenes.Min(level => level.Master);
				double clamped = Math.Clamp(masterChange, MinimumMaster - lowest, MaximumMaster - highest);
				if (clamped != masterChange) notes.Add($"The master Level stops at its limit; the trims carry the remaining {masterChange - clamped:+0.0;-0.0} dB.");
				masterChange = Math.Round(clamped, 1);
			}

			List<Fm9LevelPlan> plans = [];
			foreach (Fm9SceneLevelState level in scenes)
			{
				double ideal = level.Trim + change[level.Scene] - masterChange;
				double trim = Math.Round(Math.Clamp(ideal, Fm9Levelling.MinimumTrim, Fm9Levelling.MaximumTrim), 1);
				double applied = trim - level.Trim + masterChange;
				double predicted = Math.Round(level.PeakDecibels + applied, 1);
				List<string> sceneNotes = [];
				if (trim != Math.Round(ideal, 1))
					sceneNotes.Add($"The trim stops at {trim:+0.0;-0.0} dB, {Math.Abs(ideal - trim):0.0} dB short: change this scene's amp or block levels.");
				if (predicted > Fm9Levelling.PeakCeilingDecibels)
					sceneNotes.Add($"Its loudest moment would reach {predicted:+0.0;-0.0} dBFS.");
				plans.Add(new(level.Scene, Math.Round(level.Lufs, 2), Math.Round(level.Lufs + applied, 2), level.Trim, trim, predicted, sceneNotes));
			}
			return new(plans, masterChange, peakTarget, plans.Max(plan => plan.PredictedPeakDecibels), notes);
		}

		private static (bool Absolute, double Anchor) ParseTarget(string target, IReadOnlyList<Fm9SceneLevelState> measured)
		{
			string text = target.Trim();
			if (text.StartsWith("match", StringComparison.OrdinalIgnoreCase))
			{
				Match match = Regex.Match(text, @"^match\s*(scene\s*)?([1-8])$", RegexOptions.IgnoreCase);
				if (!match.Success) throw new ArgumentException($"Write the scene to match as \"match 1\", not '{target}'.");
				int reference = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
				return (false, measured.FirstOrDefault(level => level.Scene == reference)?.Lufs
					?? throw new ArgumentException($"Scene {reference} has not been measured, so it can't be matched."));
			}
			if (double.TryParse(text.Replace("LUFS", "", StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double loudness))
				return (true, loudness);
			throw new ArgumentException($"A target is \"match 1\" or a loudness such as \"-18\", not '{target}'.");
		}

		// The channel Output 1 plays in each of these scenes, from a preset file.
		public static IReadOnlyDictionary<int, string> OutputChannels(string presetPath, IEnumerable<int> scenes)
		{
			Fractal.Companion.Protocol.Fm9UploadedPreset preset = Fm9Presets.Load(presetPath);
			Fractal.Companion.Protocol.Fm9UploadedBlock output;
			try { output = Fm9Presets.Editable(preset, new("Output", 1)); }
			catch (ArgumentException) { throw new InvalidOperationException("The preset has no Output 1 block to level with."); }
			return scenes.Distinct().ToDictionary(scene => scene, scene => scene is >= 1 and <= 8
				? Fm9Names.Letter((int)preset.GetSceneState(output, scene).Channel)
				: throw new ArgumentOutOfRangeException(nameof(scenes), "Scenes are 1 to 8."));
		}

		// Reads each scene's Output channel trims and master as they stand.
		public static async Task<IReadOnlyDictionary<int, (string Channel, double Trim, double Master)>> CurrentAsync(Fm9Live live,
			IReadOnlyDictionary<int, string> outputChannelByScene, CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(live);
			ArgumentNullException.ThrowIfNull(outputChannelByScene);
			string[] names = [MasterSetting, .. Enumerable.Range(1, 8).Select(Fm9Levelling.TrimSetting)];
			Dictionary<int, (string, double, double)> current = [];
			foreach (IGrouping<string, KeyValuePair<int, string>> channel in outputChannelByScene.GroupBy(pair => pair.Value))
			{
				Fm9LiveBlockState state = await live.GetAsync(new("Output", 1), channel.Key, names, cancellationToken);
				double Number(string name) => double.Parse(state.Settings.GetValueOrDefault(name, "0"), CultureInfo.InvariantCulture);
				foreach (KeyValuePair<int, string> scene in channel)
					current[scene.Key] = (channel.Key, Number(Fm9Levelling.TrimSetting(scene.Key)), Number(MasterSetting));
			}
			return current;
		}

		// Applies the plan live on each Output channel the scenes play: their trims, and the master moved by the plan. A live
		// write to a channel that isn't playing would switch the block to it (changing the scene), so each channel is
		// written from a scene that plays it, and the scene that was showing is selected again afterwards.
		public static async Task<IReadOnlyList<Fm9LiveResult>> ApplyAsync(Fm9Live live, Fm9PresetLevelPlan plan,
			IReadOnlyDictionary<int, (string Channel, double Trim, double Master)> current, int showingScene, CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(live);
			ArgumentNullException.ThrowIfNull(plan);
			ArgumentNullException.ThrowIfNull(current);
			List<Fm9LiveResult> results = [];
			int selected = showingScene;
			foreach (IGrouping<string, Fm9LevelPlan> channel in plan.Scenes.GroupBy(scene => current[scene.Scene].Channel))
			{
				Dictionary<string, string> changes = channel.Where(scene => scene.NewTrim != scene.CurrentTrim)
					.ToDictionary(scene => Fm9Levelling.TrimSetting(scene.Scene), scene => scene.NewTrim.ToString("0.0", CultureInfo.InvariantCulture));
				if (plan.MasterChange != 0)
					changes[MasterSetting] = Math.Round(current[channel.First().Scene].Master + plan.MasterChange, 1).ToString("0.0", CultureInfo.InvariantCulture);
				if (changes.Count == 0) continue;
				int scene = channel.Any(item => item.Scene == selected) ? selected : channel.First().Scene;
				if (scene != selected) selected = await live.SelectSceneAsync(scene, cancellationToken);
				results.Add(await live.SetAsync(new("Output", 1), changes, channel.Key, cancellationToken));
			}
			if (selected != showingScene) await live.SelectSceneAsync(showingScene, cancellationToken);
			return results;
		}
	}
}
