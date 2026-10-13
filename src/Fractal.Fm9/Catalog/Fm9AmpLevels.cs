using System.Globalization;
using System.Text.Json;

namespace Fractal.Companion.Catalog
{
	// The Amp Level that brings each amp model to the top of the FM9 output meter, as a player levels it (the median of
	// their own leveled presets; tools/build-amp-levels.py). How loud an amp runs depends mostly on the model, so a
	// generated preset takes its model's level rather than the AI's guess, which came out 10 dB hot on a USA MK IIC+.
	public static class Fm9AmpLevels
	{
		private static readonly Lazy<(double Default, IReadOnlyDictionary<int, double> Models)> Table = new(Load);
		private const double UnmeasuredDefault = -11.0;

		public static double For(int model) =>
			Table.Value.Models.TryGetValue(model, out double level) ? level : Table.Value.Default;

		public static bool IsMeasured(int model) => Table.Value.Models.ContainsKey(model);

		private static (double, IReadOnlyDictionary<int, double>) Load()
		{
			// A build without the table (the public one: it is the medians of one player's presets) levels every model at the
			// table's own default, and FRACTONE_AMP_LEVELS (or the older FM9_AMP_LEVELS) can point at a table a user built from their own presets.
			using Stream? stream = (Environment.GetEnvironmentVariable("FRACTONE_AMP_LEVELS") ?? Environment.GetEnvironmentVariable("FM9_AMP_LEVELS")) is { Length: > 0 } path ? File.OpenRead(path)
				: typeof(Fm9AmpLevels).Assembly.GetManifestResourceStream("Fractal.Companion.Catalog.fm9-12-amp-levels.json");
			if (stream is null)
				return (UnmeasuredDefault, new Dictionary<int, double>());
			using JsonDocument document = JsonDocument.Parse(stream);
			Dictionary<int, double> models = document.RootElement.GetProperty("Models").EnumerateObject()
				.ToDictionary(model => int.Parse(model.Name, CultureInfo.InvariantCulture), model => model.Value.GetProperty("Level").GetDouble());
			return (document.RootElement.GetProperty("Default").GetDouble(), models);
		}
	}
}
