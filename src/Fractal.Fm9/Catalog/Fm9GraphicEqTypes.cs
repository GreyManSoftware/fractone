namespace Fractal.Companion.Catalog
{
	// One Graphic EQ type's sliders as FM9-Edit labels them, in band order (band N is record column N-1). Frequencies are
	// null for the tone-stack styles (passive, console, JMPRE-1), which name their bands instead.
	public sealed record Fm9GraphicEqType(string Name, IReadOnlyList<string> BandLabels, IReadOnlyList<double>? Frequencies,
		double BandwidthOctaves);

	// Read from FM9-Edit on hardware (firmware 12.00, 2026-10-04): each GEQ_TYPE written in turn and the sliders' labels
	// captured, with band columns 0, 4 and 9 confirmed against the first and last sliders. The outermost bands are shelves.
	public static class Fm9GraphicEqTypes
	{
		private static readonly double[] TenBand = [31.25, 62.5, 125, 250, 500, 1_000, 2_000, 4_000, 8_000, 16_000];
		private static readonly double[] EightBand = [80, 160, 320, 640, 1_250, 2_500, 5_000, 10_000];
		private static readonly double[] SevenBand = [100, 200, 400, 800, 1_600, 3_200, 6_400];
		private static readonly double[] FiveBand = [200, 400, 800, 1_600, 3_200];
		private static readonly double[] TwoThirdsOctave = [100, 160, 250, 400, 640, 1_000, 1_600, 2_500, 4_000, 6_400];

		public static IReadOnlyList<Fm9GraphicEqType> All { get; } =
		[
			Numbered("10 Band Constant Q", TenBand, ["31", "63", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"], 1),
			Numbered("8 Band Constant Q", EightBand, ["80", "160", "320", "640", "1250", "2500", "5000", "10k"], 1),
			Numbered("7 Band Constant Q", SevenBand, null, 1),
			Numbered("5 Band Constant Q", FiveBand, null, 1),
			Numbered("10 Band Variable Q", TenBand, ["31", "63", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"], 1),
			Numbered("8 Band Variable Q", EightBand, ["80", "160", "320", "640", "1250", "2500", "5000", "10k"], 1),
			Numbered("7 Band Variable Q", SevenBand, null, 1),
			Numbered("5 Band Variable Q", FiveBand, null, 1),
			Named("5 Band Passive", "Low", "Low Mid", "Mid", "High Mid", "High"),
			Named("4 Band Passive", "Low", "Low Mid", "High Mid", "High"),
			Named("3 Band Passive", "Low", "Mid", "High"),
			Named("3 Band Console", "Low", "Mid", "High"),
			Numbered("10 Band 2/3 Oct Const Q", TwoThirdsOctave, null, 2.0 / 3),
			Numbered("10 Band 2/3 Oct Var Q", TwoThirdsOctave, null, 2.0 / 3),
			// The Mark bands sit about 1.6 octaves apart; the Amp output EQ graph uses the same centres.
			Numbered("5 Band Mark", [80, 240, 750, 2_200, 6_600], null, 1.6),
			Named("4 Band JMPRE-1", "Bass", "Mid", "Treble", "Presence"),
			Numbered("7 Band Pedal", SevenBand, ["100", "200", "400", "800", "1.6k", "3.2k", "6.4k"], 1),
			Numbered("7 Band Bass Pedal", [62.5, 125, 250, 500, 1_000, 2_000, 4_000], ["62", "125", "250", "500", "1k", "2k", "4k"], 1)
		];

		public static Fm9GraphicEqType? For(string? typeOrdinal) =>
			int.TryParse(typeOrdinal, out int ordinal) && ordinal >= 0 && ordinal < All.Count ? All[ordinal] : null;

		private static Fm9GraphicEqType Numbered(string name, double[] frequencies, string[]? labels, double octaves) =>
			new(name, labels ?? frequencies.Select(frequency => frequency.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
				frequencies, octaves);

		private static Fm9GraphicEqType Named(string name, params string[] bands) => new(name, bands, null, 1);
	}
}
