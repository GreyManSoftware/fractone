using System.Globalization;
using Fractal.Core;

namespace Fractal.Application
{
	// The FM9 Parametric EQ as a fittable EQ: five bands (1 low shelf, 2-4 peaking, 5 high shelf at their template
	// defaults), and the proposal parameters that store them. A fit keeps each band to a musical range inside the
	// device's: shelves at the ends of the spectrum and gentle (Q 0.5-1.5), peaks from 100 Hz to 6 kHz at Q 0.4-4, and
	// above that only the high shelf, which may cut freely but boost at most 3 dB. Wider ranges let fits park a "low
	// shelf" at 1.5 kHz, Q 10 needles and fake-volume shelves, and boosts chasing a stem's bright top end added fizz.
	public static class Fm9ParametricEq
	{
		public const string BlockType = "Parametric EQ";
		// FM9 effect id of Parametric EQ 1.
		public const int EffectId = 54;
		public const int BandCount = 5;
		// The device allows ±20 dB; a match that needs more than 12 is fitting noise or bleed, not tone.
		public const double MaximumFitGainDecibels = 12;
		public const double DefaultQ = 0.707;

		public static IReadOnlyList<ParametricEqBandLimits> Limits { get; } =
		[
			new(ParametricEqShape.LowShelf, 40, 300, 0.5, 1.5),
			new(ParametricEqShape.Peaking, 100, 6_000, 0.4, 6),
			new(ParametricEqShape.Peaking, 100, 6_000, 0.4, 6),
			new(ParametricEqShape.Peaking, 100, 6_000, 0.4, 6),
			new(ParametricEqShape.HighShelf, 2_500, 12_000, 0.5, 1.5, MaximumBoostDecibels: 3)
		];

		// What an FM9 Parametric EQ channel holds before anyone edits it.
		public static IReadOnlyList<double> DefaultFrequencies { get; } = [200, 400, 800, 1_600, 3_200];

		public static IReadOnlyList<string> ParameterNames { get; } = Enumerable.Range(1, BandCount)
			.SelectMany(band => new[] { $"Frequency {band}", $"Q {band}", $"Gain {band}" }).ToArray();

		public static IReadOnlyList<ParametricEqBand> FromParameters(IReadOnlyDictionary<string, string> parameters)
		{
			ArgumentNullException.ThrowIfNull(parameters);
			return Enumerable.Range(0, BandCount).Select(band => new ParametricEqBand(Limits[band].Shape,
				Read(parameters, $"Frequency {band + 1}", DefaultFrequencies[band]),
				Read(parameters, $"Q {band + 1}", DefaultQ),
				Read(parameters, $"Gain {band + 1}", 0))).ToArray();
		}

		public static IReadOnlyDictionary<string, string> ToParameters(IReadOnlyList<ParametricEqBand> bands)
		{
			ArgumentNullException.ThrowIfNull(bands);
			if (bands.Count != BandCount)
				throw new ArgumentException($"The Parametric EQ has {BandCount} bands.", nameof(bands));
			Dictionary<string, string> parameters = new(StringComparer.Ordinal);
			for (int band = 0; band < BandCount; band++)
			{
				parameters[$"Frequency {band + 1}"] = Format(bands[band].FrequencyHz);
				parameters[$"Q {band + 1}"] = Format(bands[band].Q);
				parameters[$"Gain {band + 1}"] = Format(bands[band].GainDecibels);
			}
			return parameters;
		}

		// The EQ a take recorded in this scene went through: the channel the scene plays, or flat when it is bypassed (the
		// FM9's own default bands, so a fit starts there rather than from whatever a bypassed channel happens to hold).
		// Settings the proposal leaves unset (or a missing block) play the export template's values, given as template.
		public static IReadOnlyList<ParametricEqBand> Recorded(ToneDesignProposal proposal, int sceneNumber,
			IReadOnlyDictionary<string, string>? template = null)
		{
			ArgumentNullException.ThrowIfNull(proposal);
			IReadOnlyDictionary<string, string> Merged(IReadOnlyDictionary<string, string> parameters)
			{
				Dictionary<string, string> merged = new(template ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
				foreach ((string name, string value) in parameters)
					merged[name] = value;
				return merged;
			}
			ToneDesignBlockProposal? block = Find(proposal);
			if (block is null)
				return FromParameters(Merged(new Dictionary<string, string>()));
			if (proposal.Scenes.Count == 0)
				return FromParameters(Merged(block.Parameters));
			if (sceneNumber < 1 || sceneNumber > proposal.Scenes.Count)
				throw new ArgumentOutOfRangeException(nameof(sceneNumber), "Choose one of the preset's scenes.");
			ToneDesignSceneProposal scene = proposal.Scenes[sceneNumber - 1];
			IReadOnlyList<ParametricEqBand> bands = FromParameters(Merged(block.GetParameters(ToneDesignSceneChannels.Effective(scene, block))));
			// A bypassed or flat EQ shaped nothing, so the fit starts from the FM9's default bands rather than whatever
			// frequencies and Qs the channel happens to hold.
			return ToneDesignSceneChannels.Find(scene, block.BlockType, block.Instance)?.IsBypassed == true ||
				bands.All(band => band.GainDecibels == 0)
				? FromParameters(new Dictionary<string, string>()) : bands;
		}

		public static ToneDesignBlockProposal? Find(ToneDesignProposal proposal) => proposal.Blocks.FirstOrDefault(block =>
			block.BlockType.Equals(BlockType, StringComparison.OrdinalIgnoreCase) && block.Instance == 1);

		private static double Read(IReadOnlyDictionary<string, string> parameters, string name, double fallback)
		{
			string? text = parameters.FirstOrDefault(parameter => parameter.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
			return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
				? value : fallback;
		}

		private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
	}
}
