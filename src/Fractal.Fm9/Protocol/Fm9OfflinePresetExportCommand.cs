using System.Globalization;

namespace Fractal.Companion.Protocol
{
	public static class Fm9OfflinePresetExportCommand
	{
		private static readonly IReadOnlyDictionary<string, Fm9AmpKnob> AmpOptions =
			new Dictionary<string, Fm9AmpKnob>(StringComparer.Ordinal)
			{
				["--amp-gain"] = Fm9AmpKnob.Gain,
				["--amp-bass"] = Fm9AmpKnob.Bass,
				["--amp-mid"] = Fm9AmpKnob.Mid,
				["--amp-treble"] = Fm9AmpKnob.Treble,
				["--amp-master"] = Fm9AmpKnob.MasterVolume
			};

		public static Fm9OfflinePresetExportRequest ParseArguments(string[] args)
		{
			ArgumentNullException.ThrowIfNull(args);

			if (args.Length < 4 || !string.Equals(args[0], "--export-fm9", StringComparison.Ordinal))
			{
				throw new ArgumentException(
					"Usage: --export-fm9 <template.syx> <output.syx> <preset-name> " +
					"[--amp-gain <0-10>] [--amp-bass <0-10>] [--amp-mid <0-10>] " +
					"[--amp-treble <0-10>] [--amp-master <0-10>].");
			}

			Dictionary<Fm9AmpKnob, double> ampValues = [];

			for (int index = 4; index < args.Length; index += 2)
			{
				if (index + 1 >= args.Length || !AmpOptions.TryGetValue(args[index], out Fm9AmpKnob knob))
				{
					throw new ArgumentException(
						$"Unsupported export option '{args[index]}'. Supported options are: " +
						"--amp-gain, --amp-bass, --amp-mid, --amp-treble, and --amp-master.");
				}

				if (!double.TryParse(
					args[index + 1],
					NumberStyles.Float,
					CultureInfo.InvariantCulture,
					out double value) ||
					double.IsNaN(value) ||
					double.IsInfinity(value) ||
					value < 0 ||
					value > 10)
				{
					throw new ArgumentException(
						$"{args[index]} must be a finite number from 0 through 10.");
				}

				if (!ampValues.TryAdd(knob, value))
				{
					throw new ArgumentException($"The option '{args[index]}' was supplied more than once.");
				}
			}

			return new Fm9OfflinePresetExportRequest(
				args[1],
				args[2],
				args[3],
				ampValues);
		}
	}
}
