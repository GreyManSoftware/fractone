using System.Globalization;

namespace Fractal.Fm9
{
	public sealed record Fm9SceneLevel(int Scene, double Lufs, double PeakDecibels);

	public sealed record Fm9LevelPlan(int Scene, double MeasuredLufs, double TargetLufs, double CurrentTrim, double NewTrim, double PredictedPeakDecibels,
		IReadOnlyList<string> Notes);

	// Scene levelling with the Output block's "Scene N Level" trims, which come after every other block: a change in a
	// trim changes what Output 1 sends by the same number of dB, so one pass lands within a measurement's repeatability.
	public static class Fm9Levelling
	{
		public const double MinimumTrim = -20, MaximumTrim = 20;
		// A boost that would bring the loudest moment above this gets a warning.
		public const double PeakCeilingDecibels = -1;

		public static string TrimSetting(int scene) => $"Scene {scene} Level";

		// target: a loudness ("-18" or "-18 LUFS"), or "match N" for scene N's measured loudness. offsets add per-scene dB
		// (leads +2). currentTrims are the trims in force when the scenes were measured.
		public static IReadOnlyList<Fm9LevelPlan> Plan(IReadOnlyList<Fm9SceneLevel> measured, string target,
			IReadOnlyDictionary<int, double>? offsets, IReadOnlyDictionary<int, double> currentTrims)
		{
			ArgumentNullException.ThrowIfNull(measured);
			ArgumentException.ThrowIfNullOrWhiteSpace(target);
			if (measured.Count == 0) throw new ArgumentException("Measure at least one scene first.", nameof(measured));
			string text = target.Trim();
			double baseTarget;
			if (text.StartsWith("match", StringComparison.OrdinalIgnoreCase))
			{
				System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(text, @"^match\s*(scene\s*)?([1-8])$",
					System.Text.RegularExpressions.RegexOptions.IgnoreCase);
				if (!match.Success) throw new ArgumentException($"Write the scene to match as \"match 1\", not '{target}'.");
				int reference = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
				baseTarget = measured.FirstOrDefault(level => level.Scene == reference)?.Lufs
					?? throw new ArgumentException($"Scene {reference} has not been measured, so it can't be matched.");
			}
			else if (!double.TryParse(text.Replace("LUFS", "", StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out baseTarget))
				throw new ArgumentException($"A target is a loudness such as \"-18\" or \"match 1\", not '{target}'.");

			List<Fm9LevelPlan> plans = [];
			foreach (Fm9SceneLevel level in measured.OrderBy(level => level.Scene))
			{
				if (level.Scene is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(measured), "Scenes are 1 to 8.");
				if (!double.IsFinite(level.Lufs)) throw new ArgumentException($"Scene {level.Scene}'s measurement has no loudness (was anything playing?).");
				double wanted = baseTarget + (offsets?.GetValueOrDefault(level.Scene) ?? 0);
				double current = currentTrims.GetValueOrDefault(level.Scene);
				double ideal = current + (wanted - level.Lufs);
				double trim = Math.Round(Math.Clamp(ideal, MinimumTrim, MaximumTrim), 1);
				double peak = level.PeakDecibels + (trim - current);
				List<string> notes = [];
				if (trim != Math.Round(ideal, 1))
					notes.Add($"The trim stops at {trim:+0.0;-0.0} dB, {Math.Abs(ideal - trim):0.0} dB short of the target: change the amp or block levels for this scene.");
				if (peak > PeakCeilingDecibels)
					notes.Add($"Its loudest moment would reach {peak:+0.0;-0.0} dBFS: lower the Output's master Level by {peak - PeakCeilingDecibels:0.0} dB (it moves every scene together) or aim lower.");
				plans.Add(new(level.Scene, Math.Round(level.Lufs, 2), Math.Round(wanted, 2), current, trim, Math.Round(peak, 1), notes));
			}
			return plans;
		}
	}
}
