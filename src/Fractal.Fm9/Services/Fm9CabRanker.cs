using System.Globalization;
using System.Text.Json;
using Fractal.Application;
using Fractal.Companion.Catalog;
using Fractal.Core;

namespace Fractal.Fm9
{
	// Which cabs to rank: the factory IRs, the DynaCab placements that were measured, or both.
	public enum Fm9CabSource
	{
		Factory,
		DynaCab,
		Both
	}

	// A measured DynaCab placement: type and mic as the Cab block names them, "Dyna Cab" position (0-10) and Distance (cm).
	public sealed record Fm9DynaCabPlacement(string Type, string Mic, int Position, int DistanceCentimetres);

	// Bank and Type are -1 for a DynaCab placement, which DynaCab names instead.
	public sealed record Fm9CabCandidate(int Bank, int Type, string Name, double GapDecibels, double GapWithoutEqDecibels,
		IReadOnlyList<ParametricEqBand> Eq, Fm9DynaCabPlacement? DynaCab = null)
	{
		// The Cab channel settings that play this cab from slot 1 alone, by name as set_parameters and set_channel take them.
		// Mode is always set: a channel copied from one in the other mode would otherwise keep playing the old cab.
		public IReadOnlyDictionary<string, string> CabSettings => DynaCab is { } placement
			? new Dictionary<string, string>
			{
				["Mode"] = "DYNA-CAB", ["Dynacab Type1"] = placement.Type, ["Dynacab Mic1"] = placement.Mic,
				["Dyna Cab"] = placement.Position.ToString(CultureInfo.InvariantCulture),
				["Distance"] = placement.DistanceCentimetres.ToString(CultureInfo.InvariantCulture), ["Mute2"] = "MUTE"
			}
			: new Dictionary<string, string>
			{
				["Mode"] = "LEGACY", ["Bank"] = Bank.ToString(CultureInfo.InvariantCulture),
				["Cab Type"] = Type.ToString(CultureInfo.InvariantCulture), ["Mute2"] = "MUTE"
			};
	}

	// The cab to play for a stem: the best factory cab, the best measured DynaCab placement (when allowed) and the one chosen,
	// which is the DynaCab only when its gap beats the best factory cab's.
	public sealed record Fm9CabPick(Fm9CabCandidate? Factory, Fm9CabCandidate? DynaCab, Fm9CabCandidate Chosen)
	{
		public bool ChoosesDynaCab => Chosen.DynaCab is not null;
	}

	// Ranks every factory cab for a song: which one cab, with a gentle Parametric EQ after it, comes closest to an isolated
	// stem. A cab is a fixed filter, so one re-amp of the scene with its Cab (and any EQ after it) bypassed predicts them
	// all: take + the cab's measured curve (fm9-12-cab-curves.json) + the fitted EQ, against the stem. Checked on the FM9
	// (Ride, 2026-10-14): two predictions re-amped for real came within 0.04 dB of the measured gap. DynaCab placements
	// (fm9-12-dynacab-curves.json) were measured on the same rig, so they rank on the same scale. The gap is a shortlist,
	// not a verdict: it rewards the low-mid body a stem carries from bleed and mastering, and players chose by ear.
	public static class Fm9CabRanker
	{
		// Gentle: no EQ move beyond this, and only broad bands (GentleLimits). Fits allowed 12 dB or Q 6 put a needle on
		// every band of every cab and stopped telling cabs apart; the cab should do the shaping.
		public const double DefaultMaximumEqDecibels = 3;

		public static IReadOnlyList<ParametricEqBandLimits> GentleLimits { get; } = Fm9ParametricEq.Limits
			.Select(limit => limit.Shape == ParametricEqShape.Peaking ? limit with { MaximumQ = 2 } : limit).ToArray();
		// Cabs whose cheap estimate is this far down the list are not worth a full EQ fit.
		private const int FittedCandidates = 80;
		// Half-width of each analysis band (a 1/6-octave band, wide enough to hold an FFT bin at 60 Hz).
		private const double HalfBandOctaves = 1.0 / 12;
		private const int DeviceSampleRate = 48_000;
		private static readonly Lazy<Curves> Bundled = new(Load);

		// How many cabs are fitted at once. A quarter of the cores leaves the PC usable while a ranking runs; tests lower it
		// so a suite running beside others stays light.
		public static int MaxParallelism
		{
			get => _maxParallelism;
			set => _maxParallelism = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), "At least one cab is fitted at a time.");
		}

		private static int _maxParallelism = Math.Max(1, Environment.ProcessorCount / 4);

		// The frequencies the bundled curves are measured at (60 Hz to 10 kHz, twelve per octave).
		public static IReadOnlyList<double> Frequencies => Bundled.Value.Frequencies;

		// A cab's measured response (dB, relative to its mean) at Frequencies.
		public static IReadOnlyList<double> Curve(int bank, int type) =>
			Bundled.Value.Cabs.Single(cab => cab.DynaCab is null && cab.Bank == bank && cab.Type == type).Decibels;

		// A measured DynaCab placement's response (dB, relative to its mean) at Frequencies.
		public static IReadOnlyList<double> Curve(Fm9DynaCabPlacement placement) =>
			Bundled.Value.Cabs.Single(cab => cab.DynaCab == placement).Decibels;

		// Every DynaCab placement with a measured curve.
		public static IReadOnlyList<Fm9DynaCabPlacement> DynaCabPlacements =>
			Bundled.Value.Cabs.Where(cab => cab.DynaCab is not null).Select(cab => cab.DynaCab!).ToArray();

		public static IReadOnlyList<Fm9CabCandidate> Rank(MultichannelAudioBuffer noCabTake, MultichannelAudioBuffer stem,
			int count = 10, string? nameFilter = null, double maximumEqDecibels = DefaultMaximumEqDecibels,
			Fm9CabSource include = Fm9CabSource.Factory)
		{
			ArgumentNullException.ThrowIfNull(noCabTake);
			ArgumentNullException.ThrowIfNull(stem);
			double top = Math.Min(10_000, Math.Min(noCabTake.SampleRate, stem.SampleRate) * 0.45);
			double[] frequencies = Frequencies.Where(frequency => frequency <= top).ToArray();
			double[] take = WetToneMatcher.ActiveSpectrumDecibels(noCabTake, frequencies, HalfBandOctaves);
			double[] target = WetToneMatcher.ActiveSpectrumDecibels(stem, frequencies, HalfBandOctaves);
			return Rank(frequencies.Select((_, index) => target[index] - take[index]).ToArray(), count, nameFilter, maximumEqDecibels, include);
		}

		// Ranks from the difference (dB) the cab and EQ must make, stem minus no-cab take, at the first need.Length of
		// Frequencies. Factory cabs only by default: DynaCab placements are an option until players' ears say mixing helps.
		public static IReadOnlyList<Fm9CabCandidate> Rank(IReadOnlyList<double> need, int count = 10, string? nameFilter = null,
			double maximumEqDecibels = DefaultMaximumEqDecibels, Fm9CabSource include = Fm9CabSource.Factory) =>
			Rank(need, count, nameFilter, maximumEqDecibels, include, roster: null);

		// Ranks only the cabs the roster admits (bank, type, DynaCab placement). Each fit is costly, so tests rank a target
		// among a few decoys; every caller in the product ranks the whole roster.
		internal static IReadOnlyList<Fm9CabCandidate> Rank(IReadOnlyList<double> need, int count, string? nameFilter,
			double maximumEqDecibels, Fm9CabSource include, Func<int, int, Fm9DynaCabPlacement?, bool>? roster)
		{
			ArgumentNullException.ThrowIfNull(need);
			Curves curves = Bundled.Value;
			// Below 90 Hz isolated stems carry bass and kick bleed, as the tone matcher's guard assumes.
			int[] used = Enumerable.Range(0, need.Count)
				.Where(index => curves.Frequencies[index] >= ParametricEqMatchSettings.DefaultGuardHz).ToArray();
			double[] frequencies = used.Select(index => curves.Frequencies[index]).ToArray();
			double[] weights = frequencies.Select(WetToneMatcher.StemTrust).ToArray();

			(Cab Cab, double[] Wanted, double Raw, double Estimate)[] cabs = curves.Cabs
				.Where(cab => include == Fm9CabSource.Both || (cab.DynaCab is null) == (include == Fm9CabSource.Factory))
				.Where(cab => nameFilter is null || cab.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
				.Where(cab => roster is null || roster(cab.Bank, cab.Type, cab.DynaCab))
				.Select(cab =>
				{
					double[] wanted = used.Select(index => need[index] - cab.Decibels[index]).ToArray();
					return (Cab: cab, Wanted: wanted, Raw: Gap(Centre(wanted, weights), weights), Estimate: Gap(Smooth(wanted, frequencies, weights), weights));
				}).ToArray();
			if (cabs.Length == 0)
				throw new ArgumentException($"No {(include == Fm9CabSource.Factory ? "factory cab" : include == Fm9CabSource.DynaCab ? "DynaCab placement" : "cab")}'s name contains '{nameFilter}'.", nameof(nameFilter));
			IReadOnlyList<ParametricEqBand> flat = Fm9ParametricEq.Limits.Select((limit, band) =>
				new ParametricEqBand(limit.Shape, Fm9ParametricEq.DefaultFrequencies[band], Fm9ParametricEq.DefaultQ, 0)).ToArray();
			return cabs.OrderBy(cab => cab.Estimate).Take(Math.Max(FittedCandidates, count))
				.AsParallel().WithDegreeOfParallelism(MaxParallelism)
				.Select(cab =>
				{
					IReadOnlyList<ParametricEqBand> eq = ParametricEqFitter.Fit(frequencies, cab.Wanted, GentleLimits, flat,
						maximumEqDecibels, DeviceSampleRate, weights);
					double[] response = ParametricEqResponse.Decibels(eq, frequencies, DeviceSampleRate);
					double gap = Gap(Centre(cab.Wanted.Select((value, index) => value - response[index]).ToArray(), weights), weights);
					return new Fm9CabCandidate(cab.Cab.Bank, cab.Cab.Type, cab.Cab.Name, Math.Round(gap, 2), Math.Round(cab.Raw, 2), eq, cab.Cab.DynaCab);
				})
				.OrderBy(candidate => candidate.GapDecibels).ThenBy(candidate => candidate.GapWithoutEqDecibels).Take(count).ToArray();
		}

		// Factory and DynaCab ranked together (include Both), so DynaCab wins only on a smaller gap; without DynaCab allowed
		// only the factory cabs are ranked.
		public static Fm9CabPick Pick(IReadOnlyList<double> need, bool allowDynaCab, double maximumEqDecibels = DefaultMaximumEqDecibels) =>
			Pick(need, allowDynaCab, maximumEqDecibels, roster: null);

		internal static Fm9CabPick Pick(IReadOnlyList<double> need, bool allowDynaCab, double maximumEqDecibels,
			Func<int, int, Fm9DynaCabPlacement?, bool>? roster)
		{
			IReadOnlyList<Fm9CabCandidate> ranked = Rank(need, FittedCandidates, null, maximumEqDecibels,
				allowDynaCab ? Fm9CabSource.Both : Fm9CabSource.Factory, roster);
			return new(ranked.FirstOrDefault(candidate => candidate.DynaCab is null),
				ranked.FirstOrDefault(candidate => candidate.DynaCab is not null), ranked[0]);
		}

		// From a take recorded through the preset (its cab, then the Parametric EQ after it): the no-cab take is the wet take
		// less that cab's measured curve and that EQ, since both are fixed filters. Null when the playing cab was never
		// measured (a user IR, or a DynaCab placement off the measured grid).
		public static Fm9CabPick? PickForWetTake(MultichannelAudioBuffer wetTake, MultichannelAudioBuffer stem,
			IReadOnlyList<double>? playingCabCurve, IReadOnlyList<ParametricEqBand> eqAfterCab, bool allowDynaCab)
		{
			ArgumentNullException.ThrowIfNull(wetTake);
			ArgumentNullException.ThrowIfNull(stem);
			ArgumentNullException.ThrowIfNull(eqAfterCab);
			if (playingCabCurve is null)
				return null;
			double top = Math.Min(10_000, Math.Min(wetTake.SampleRate, stem.SampleRate) * 0.45);
			double[] frequencies = Frequencies.Where(frequency => frequency <= top).ToArray();
			double[] take = WetToneMatcher.ActiveSpectrumDecibels(wetTake, frequencies, HalfBandOctaves);
			double[] target = WetToneMatcher.ActiveSpectrumDecibels(stem, frequencies, HalfBandOctaves);
			double[] eq = ParametricEqResponse.Decibels(eqAfterCab, frequencies, DeviceSampleRate);
			return Pick(frequencies.Select((_, index) => target[index] - (take[index] - playingCabCurve[index] - eq[index])).ToArray(), allowDynaCab);
		}

		// The measured curve of the cab these Cab settings play (factory bank and type, or a measured DynaCab placement), or
		// null when it was never measured.
		public static IReadOnlyList<double>? PlayingCurve(int bank, int type, Fm9DynaCabPlacement? dynaCab)
		{
			Curves curves = Bundled.Value;
			Cab? cab = dynaCab is not null
				? curves.Cabs.FirstOrDefault(candidate => candidate.DynaCab is { } placement &&
					string.Equals(placement.Type, dynaCab.Type, StringComparison.OrdinalIgnoreCase) &&
					string.Equals(placement.Mic, dynaCab.Mic, StringComparison.OrdinalIgnoreCase) &&
					placement.Position == dynaCab.Position && placement.DistanceCentimetres == dynaCab.DistanceCentimetres)
				: curves.Cabs.FirstOrDefault(candidate => candidate.DynaCab is null && candidate.Bank == bank && candidate.Type == type);
			return cab?.Decibels;
		}

		// Weighted RMS, in dB, of a level-free difference.
		private static double Gap(double[] residual, double[] weights) =>
			Math.Sqrt(residual.Select((value, index) => value * value * weights[index]).Sum() / weights.Sum());

		private static double[] Centre(double[] values, double[] weights)
		{
			double mean = values.Select((value, index) => value * weights[index]).Sum() / weights.Sum();
			return values.Select(value => value - mean).ToArray();
		}

		// What is left after the broad trends a gentle EQ can follow (a cubic in log frequency, weighted): a quick stand-in
		// for the full fit, good enough to pick which cabs to fit.
		private static double[] Smooth(double[] values, double[] frequencies, double[] weights)
		{
			const int terms = 4;
			double centre = Math.Log2(1000);
			double[][] basis = frequencies.Select(frequency => Enumerable.Range(0, terms)
				.Select(power => Math.Pow((Math.Log2(frequency) - centre) / 3, power)).ToArray()).ToArray();
			double[,] normal = new double[terms, terms];
			double[] right = new double[terms];
			for (int point = 0; point < values.Length; point++)
				for (int row = 0; row < terms; row++)
				{
					right[row] += weights[point] * basis[point][row] * values[point];
					for (int column = 0; column < terms; column++)
						normal[row, column] += weights[point] * basis[point][row] * basis[point][column];
				}
			double[] coefficients = Solve(normal, right);
			return values.Select((value, point) => value - basis[point].Select((term, index) => term * coefficients[index]).Sum()).ToArray();
		}

		// Gaussian elimination with partial pivoting; the normal matrix is small and well conditioned.
		private static double[] Solve(double[,] matrix, double[] vector)
		{
			int size = vector.Length;
			double[,] a = (double[,])matrix.Clone();
			double[] b = (double[])vector.Clone();
			for (int column = 0; column < size; column++)
			{
				int pivot = Enumerable.Range(column, size - column).MaxBy(row => Math.Abs(a[row, column]));
				for (int index = 0; index < size; index++)
					(a[column, index], a[pivot, index]) = (a[pivot, index], a[column, index]);
				(b[column], b[pivot]) = (b[pivot], b[column]);
				for (int row = column + 1; row < size; row++)
				{
					double factor = a[row, column] / a[column, column];
					for (int index = column; index < size; index++)
						a[row, index] -= factor * a[column, index];
					b[row] -= factor * b[column];
				}
			}
			double[] solution = new double[size];
			for (int row = size - 1; row >= 0; row--)
			{
				double sum = b[row];
				for (int index = row + 1; index < size; index++)
					sum -= a[row, index] * solution[index];
				solution[row] = sum / a[row, row];
			}
			return solution;
		}

		private sealed record Cab(int Bank, int Type, string Name, double[] Decibels, Fm9DynaCabPlacement? DynaCab = null);

		private sealed record Curves(double[] Frequencies, Cab[] Cabs);

		private static Curves Load()
		{
			using Stream stream = typeof(Fm9CabRanker).Assembly.GetManifestResourceStream("Fractal.Companion.Catalog.fm9-12-cab-curves.json")
				?? throw new InvalidOperationException("The bundled cab curves are unavailable.");
			using JsonDocument document = JsonDocument.Parse(stream);
			double[] frequencies = document.RootElement.GetProperty("frequencies").EnumerateArray().Select(value => value.GetDouble()).ToArray();
			Fm9Firmware12Catalog catalog = Fm9Firmware12Catalog.LoadBundledAsync().GetAwaiter().GetResult();
			List<Cab> cabs = [];
			foreach (JsonProperty bank in document.RootElement.GetProperty("banks").EnumerateObject())
				foreach (JsonProperty cab in bank.Value.EnumerateObject())
				{
					int bankNumber = int.Parse(bank.Name, CultureInfo.InvariantCulture), type = int.Parse(cab.Name, CultureInfo.InvariantCulture);
					cabs.Add(new Cab(bankNumber, type, catalog.ResolveCabName(bankNumber, type),
						cab.Value.EnumerateArray().Select(value => value.GetInt32() / 10.0).ToArray()));
				}
			using Stream dynaStream = typeof(Fm9CabRanker).Assembly.GetManifestResourceStream("Fractal.Companion.Catalog.fm9-12-dynacab-curves.json")
				?? throw new InvalidOperationException("The bundled DynaCab curves are unavailable.");
			using JsonDocument dyna = JsonDocument.Parse(dynaStream);
			// Both files are written by tools/build-cab-curves.py on one grid; a mismatch would compare unlike bands.
			if (!dyna.RootElement.GetProperty("frequencies").EnumerateArray().Select(value => value.GetDouble()).SequenceEqual(frequencies))
				throw new InvalidDataException("The DynaCab curves are not on the factory cab curves' frequencies.");
			foreach (JsonElement placement in dyna.RootElement.GetProperty("placements").EnumerateArray())
			{
				Fm9DynaCabPlacement where = new(placement.GetProperty("type").GetString()!, placement.GetProperty("mic").GetString()!,
					placement.GetProperty("position").GetInt32(), placement.GetProperty("distanceCm").GetInt32());
				cabs.Add(new Cab(-1, -1, $"DynaCab {where.Type}, {where.Mic}, position {where.Position}, {where.DistanceCentimetres} cm",
					placement.GetProperty("decibels").EnumerateArray().Select(value => value.GetInt32() / 10.0).ToArray(), where));
			}
			return new Curves(frequencies, [.. cabs]);
		}
	}
}
