using System.Numerics;

namespace Fractal.Core
{
	public enum ParametricEqShape
	{
		LowShelf,
		Peaking,
		HighShelf
	}

	public sealed record ParametricEqBand(ParametricEqShape Shape, double FrequencyHz, double Q, double GainDecibels);

	// Where a fit may put a band: a frequency range inside the device's own range for that band, a Q range, and how far
	// it may boost (cuts are bounded only by the fit's overall maximum gain).
	public sealed record ParametricEqBandLimits(ParametricEqShape Shape, double MinimumHz, double MaximumHz,
		double MinimumQ = ParametricEqFitter.MinimumQ, double MaximumQ = ParametricEqFitter.MaximumQ,
		double MaximumBoostDecibels = double.PositiveInfinity);

	// RBJ-cookbook biquads (the formulas the designer's EQ graph draws). Negating a band's gain inverts it exactly, which
	// is how a preview removes the EQ a recording was made with before applying a new one.
	public static class ParametricEqResponse
	{
		public static double[] Decibels(IReadOnlyList<ParametricEqBand> bands, IReadOnlyList<double> frequencies, int sampleRate)
		{
			ArgumentNullException.ThrowIfNull(bands);
			ArgumentNullException.ThrowIfNull(frequencies);
			Coefficients[] filters = bands.Select(band => CoefficientsFor(band, sampleRate)).ToArray();
			double[] response = new double[frequencies.Count];
			for (int index = 0; index < response.Length; index++)
			{
				Complex delay = Complex.FromPolarCoordinates(1, -2 * Math.PI * frequencies[index] / sampleRate);
				Complex delaySquared = delay * delay;
				double total = 0;
				foreach (Coefficients filter in filters)
				{
					total += 20 * Math.Log10(((filter.B0 + filter.B1 * delay + filter.B2 * delaySquared) /
						(1 + filter.A1 * delay + filter.A2 * delaySquared)).Magnitude);
				}
				response[index] = total;
			}
			return response;
		}

		public static MultichannelAudioBuffer Apply(MultichannelAudioBuffer source, IReadOnlyList<ParametricEqBand> bands)
		{
			ArgumentNullException.ThrowIfNull(source);
			ArgumentNullException.ThrowIfNull(bands);
			float[][] channels = source.Channels.Select(channel => channel.ToArray()).ToArray();
			foreach (float[] channel in channels)
			{
				foreach (ParametricEqBand band in bands)
				{
					Coefficients filter = CoefficientsFor(band, source.SampleRate);
					double state1 = 0;
					double state2 = 0;
					for (int index = 0; index < channel.Length; index++)
					{
						double input = channel[index];
						double output = filter.B0 * input + state1;
						state1 = filter.B1 * input - filter.A1 * output + state2;
						state2 = filter.B2 * input - filter.A2 * output;
						channel[index] = (float)output;
					}
				}
			}
			return new MultichannelAudioBuffer(source.SampleRate, channels);
		}

		public static ParametricEqBand Inverse(ParametricEqBand band) => band with { GainDecibels = -band.GainDecibels };

		internal static Complex[] Delays(IReadOnlyList<double> frequencies, int sampleRate) =>
			frequencies.Select(frequency => Complex.FromPolarCoordinates(1, -2 * Math.PI * frequency / sampleRate)).ToArray();

		internal static void BandDecibels(ParametricEqBand band, Complex[] delays, int sampleRate, double[] into)
		{
			Coefficients filter = CoefficientsFor(band, sampleRate);
			for (int index = 0; index < delays.Length; index++)
			{
				Complex delay = delays[index];
				Complex delaySquared = delay * delay;
				into[index] = 20 * Math.Log10(((filter.B0 + filter.B1 * delay + filter.B2 * delaySquared) /
					(1 + filter.A1 * delay + filter.A2 * delaySquared)).Magnitude);
			}
		}

		private sealed record Coefficients(double B0, double B1, double B2, double A1, double A2);

		private static Coefficients CoefficientsFor(ParametricEqBand band, int sampleRate)
		{
			double amplitude = Math.Pow(10, band.GainDecibels / 40);
			double omega = 2 * Math.PI * Math.Min(band.FrequencyHz, sampleRate * 0.499) / sampleRate;
			double cosine = Math.Cos(omega);
			double alpha = Math.Sin(omega) / (2 * band.Q);
			double b0, b1, b2, a0, a1, a2;
			if (band.Shape == ParametricEqShape.Peaking)
			{
				b0 = 1 + alpha * amplitude;
				b1 = -2 * cosine;
				b2 = 1 - alpha * amplitude;
				a0 = 1 + alpha / amplitude;
				a1 = -2 * cosine;
				a2 = 1 - alpha / amplitude;
			}
			else
			{
				double shelf = 2 * Math.Sqrt(amplitude) * alpha;
				double sign = band.Shape == ParametricEqShape.LowShelf ? 1 : -1;
				b0 = amplitude * ((amplitude + 1) - sign * (amplitude - 1) * cosine + shelf);
				b1 = sign * 2 * amplitude * ((amplitude - 1) - sign * (amplitude + 1) * cosine);
				b2 = amplitude * ((amplitude + 1) - sign * (amplitude - 1) * cosine - shelf);
				a0 = (amplitude + 1) + sign * (amplitude - 1) * cosine + shelf;
				a1 = -sign * 2 * ((amplitude - 1) + sign * (amplitude + 1) * cosine);
				a2 = (amplitude + 1) + sign * (amplitude - 1) * cosine - shelf;
			}
			return new Coefficients(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
		}
	}

	// Fits every band's frequency, Q and gain to a wanted response (in dB) by bounded Levenberg-Marquardt from several
	// starts. The overall level is free, as in the matcher: only the shape is fitted. Frequencies can carry weights (how
	// far the wanted response is trusted there). Results are rounded to what the device stores (whole Hz, Q to 0.01,
	// gain to 0.1 dB).
	public static class ParametricEqFitter
	{
		public const double MinimumQ = 0.1;
		public const double MaximumQ = 10;
		private const int RandomStarts = 24;
		private const int MaximumIterations = 150;
		// A pull towards 0 dB, scaled by the number of fitted points so it means the same on any grid: with the level free,
		// a weak pull let the fit spend large shelves on what was really an overall level change.
		public const double GainRegularization = 0.02;

		public static IReadOnlyList<ParametricEqBand> Fit(
			IReadOnlyList<double> frequencies,
			IReadOnlyList<double> wantedDecibels,
			IReadOnlyList<ParametricEqBandLimits> limits,
			IReadOnlyList<ParametricEqBand> start,
			double maximumGainDecibels,
			int sampleRate,
			IReadOnlyList<double>? weights = null)
		{
			ArgumentNullException.ThrowIfNull(frequencies);
			ArgumentNullException.ThrowIfNull(wantedDecibels);
			ArgumentNullException.ThrowIfNull(limits);
			ArgumentNullException.ThrowIfNull(start);
			if (frequencies.Count != wantedDecibels.Count || frequencies.Count < 4)
				throw new ArgumentException("Fit at least four frequencies, each with a wanted response.", nameof(wantedDecibels));
			if (limits.Count == 0 || start.Count != limits.Count)
				throw new ArgumentException("Give one start band for each band's limits.", nameof(start));
			if (!(maximumGainDecibels > 0))
				throw new ArgumentOutOfRangeException(nameof(maximumGainDecibels));
			if (weights is not null && (weights.Count != frequencies.Count || weights.Any(weight => !(weight >= 0)) || !(weights.Sum() > 0)))
				throw new ArgumentException("Give one non-negative weight per frequency.", nameof(weights));

			int bandCount = limits.Count;
			double[] lower = new double[bandCount * 3];
			double[] upper = new double[bandCount * 3];
			for (int band = 0; band < bandCount; band++)
			{
				lower[band * 3] = Math.Log10(limits[band].MinimumHz);
				upper[band * 3] = Math.Log10(limits[band].MaximumHz);
				lower[band * 3 + 1] = Math.Log10(limits[band].MinimumQ);
				upper[band * 3 + 1] = Math.Log10(limits[band].MaximumQ);
				lower[band * 3 + 2] = -maximumGainDecibels;
				upper[band * 3 + 2] = Math.Min(maximumGainDecibels, limits[band].MaximumBoostDecibels);
			}
			Problem problem = new(frequencies.ToArray(), wantedDecibels.ToArray(),
				(weights ?? Enumerable.Repeat(1.0, frequencies.Count)).ToArray(), limits, sampleRate);

			List<double[]> starts = [Clamp(Encode(start), lower, upper)];
			Random random = new(1984);
			for (int index = 0; index < RandomStarts; index++)
			{
				double[] candidate = new double[lower.Length];
				for (int parameter = 0; parameter < candidate.Length; parameter++)
					candidate[parameter] = parameter % 3 == 2 ? 0 : lower[parameter] + (upper[parameter] - lower[parameter]) * random.NextDouble();
				starts.Add(candidate);
			}

			double[]? best = null;
			double bestCost = double.PositiveInfinity;
			foreach (double[] candidate in starts)
			{
				double[] fitted = Solve(problem, candidate, lower, upper);
				double cost = problem.Cost(fitted);
				if (cost < bestCost)
				{
					bestCost = cost;
					best = fitted;
				}
			}
			return Round(problem.Decode(best!), limits, maximumGainDecibels);
		}

		// Average deviation (dB, level-matched) between a wanted response and what the bands produce.
		public static double Deviation(IReadOnlyList<double> frequencies, IReadOnlyList<double> wantedDecibels,
			IReadOnlyList<ParametricEqBand> bands, int sampleRate)
		{
			double[] response = ParametricEqResponse.Decibels(bands, frequencies, sampleRate);
			double[] residual = wantedDecibels.Select((wanted, index) => wanted - response[index]).ToArray();
			double mean = residual.Average();
			return Math.Sqrt(residual.Average(value => (value - mean) * (value - mean)));
		}

		// Keeps each band's response so a finite-difference step recomputes only the band it moves.
		private sealed class Problem(double[] frequencies, double[] wanted, double[] weights,
			IReadOnlyList<ParametricEqBandLimits> limits, int sampleRate)
		{
			private readonly Complex[] _delays = ParametricEqResponse.Delays(frequencies, sampleRate);
			private readonly double _weightSum = weights.Sum();
			private readonly double _regularization = GainRegularization * Math.Sqrt(frequencies.Length);

			public int ResidualCount => frequencies.Length + limits.Count;

			public ParametricEqBand Band(double[] parameters, int band) => new(limits[band].Shape,
				Math.Pow(10, parameters[band * 3]), Math.Pow(10, parameters[band * 3 + 1]), parameters[band * 3 + 2]);

			public ParametricEqBand[] Decode(double[] parameters) =>
				Enumerable.Range(0, limits.Count).Select(band => Band(parameters, band)).ToArray();

			public double[][] BandResponses(double[] parameters)
			{
				double[][] responses = new double[limits.Count][];
				for (int band = 0; band < limits.Count; band++)
				{
					responses[band] = new double[frequencies.Length];
					ParametricEqResponse.BandDecibels(Band(parameters, band), _delays, sampleRate, responses[band]);
				}
				return responses;
			}

			public double[] BandResponse(double[] parameters, int band)
			{
				double[] response = new double[frequencies.Length];
				ParametricEqResponse.BandDecibels(Band(parameters, band), _delays, sampleRate, response);
				return response;
			}

			public double[] Residuals(double[] parameters) => Residuals(parameters, BandResponses(parameters), -1, null);

			// The residuals with one band's response replaced (band -1: none), for finite differences.
			public double[] Residuals(double[] parameters, double[][] responses, int replacedBand, double[]? replacement)
			{
				double[] residuals = new double[ResidualCount];
				double mean = 0;
				for (int index = 0; index < frequencies.Length; index++)
				{
					double response = 0;
					for (int band = 0; band < responses.Length; band++)
						response += band == replacedBand ? replacement![index] : responses[band][index];
					residuals[index] = wanted[index] - response;
					mean += weights[index] * residuals[index];
				}
				mean /= _weightSum;
				for (int index = 0; index < frequencies.Length; index++)
					residuals[index] = Math.Sqrt(weights[index]) * (residuals[index] - mean);
				for (int band = 0; band < limits.Count; band++)
					residuals[frequencies.Length + band] = _regularization * parameters[band * 3 + 2];
				return residuals;
			}

			public double Cost(double[] parameters) => Residuals(parameters).Sum(value => value * value);
		}

		private static double[] Solve(Problem problem, double[] start, double[] lower, double[] upper)
		{
			double[] parameters = (double[])start.Clone();
			double[] residuals = problem.Residuals(parameters);
			double cost = residuals.Sum(value => value * value);
			double damping = 1e-3;
			int count = parameters.Length;
			for (int iteration = 0; iteration < MaximumIterations; iteration++)
			{
				double[,] jacobian = new double[residuals.Length, count];
				double[][] responses = problem.BandResponses(parameters);
				for (int parameter = 0; parameter < count; parameter++)
				{
					double step = parameter % 3 == 2 ? 1e-3 : 1e-5;
					double[] shifted = (double[])parameters.Clone();
					shifted[parameter] = Math.Min(upper[parameter], parameters[parameter] + step);
					if (shifted[parameter] == parameters[parameter])
						shifted[parameter] = parameters[parameter] - step;
					double delta = shifted[parameter] - parameters[parameter];
					double[] moved = problem.Residuals(shifted, responses, parameter / 3, problem.BandResponse(shifted, parameter / 3));
					for (int index = 0; index < residuals.Length; index++)
						jacobian[index, parameter] = (moved[index] - residuals[index]) / delta;
				}
				double[,] normal = new double[count, count];
				double[] gradient = new double[count];
				for (int row = 0; row < count; row++)
				{
					for (int index = 0; index < residuals.Length; index++)
						gradient[row] += jacobian[index, row] * residuals[index];
					for (int column = row; column < count; column++)
					{
						double sum = 0;
						for (int index = 0; index < residuals.Length; index++)
							sum += jacobian[index, row] * jacobian[index, column];
						normal[row, column] = normal[column, row] = sum;
					}
				}
				bool improved = false;
				for (int attempt = 0; attempt < 10 && !improved; attempt++)
				{
					double[,] damped = (double[,])normal.Clone();
					for (int index = 0; index < count; index++)
						damped[index, index] += damping * Math.Max(normal[index, index], 1e-9);
					double[]? step = SolveLinear(damped, gradient.Select(value => -value).ToArray());
					if (step is null)
					{
						damping *= 4;
						continue;
					}
					double[] candidate = Clamp(parameters.Select((value, index) => value + step[index]).ToArray(), lower, upper);
					double[] candidateResiduals = problem.Residuals(candidate);
					double candidateCost = candidateResiduals.Sum(value => value * value);
					if (candidateCost < cost)
					{
						improved = true;
						double gain = cost - candidateCost;
						parameters = candidate;
						residuals = candidateResiduals;
						cost = candidateCost;
						damping = Math.Max(damping / 3, 1e-9);
						if (gain < 1e-10 * Math.Max(cost, 1e-12))
							return parameters;
					}
					else
					{
						damping *= 4;
					}
				}
				if (!improved)
					break;
			}
			return parameters;
		}

		private static double[]? SolveLinear(double[,] matrix, double[] vector)
		{
			int size = vector.Length;
			double[,] a = (double[,])matrix.Clone();
			double[] b = (double[])vector.Clone();
			for (int pivot = 0; pivot < size; pivot++)
			{
				int best = pivot;
				for (int row = pivot + 1; row < size; row++)
					if (Math.Abs(a[row, pivot]) > Math.Abs(a[best, pivot]))
						best = row;
				if (Math.Abs(a[best, pivot]) < 1e-14)
					return null;
				if (best != pivot)
				{
					for (int column = 0; column < size; column++)
						(a[pivot, column], a[best, column]) = (a[best, column], a[pivot, column]);
					(b[pivot], b[best]) = (b[best], b[pivot]);
				}
				for (int row = pivot + 1; row < size; row++)
				{
					double factor = a[row, pivot] / a[pivot, pivot];
					for (int column = pivot; column < size; column++)
						a[row, column] -= factor * a[pivot, column];
					b[row] -= factor * b[pivot];
				}
			}
			double[] result = new double[size];
			for (int row = size - 1; row >= 0; row--)
			{
				double sum = b[row];
				for (int column = row + 1; column < size; column++)
					sum -= a[row, column] * result[column];
				result[row] = sum / a[row, row];
			}
			return result.All(double.IsFinite) ? result : null;
		}

		private static double[] Encode(IReadOnlyList<ParametricEqBand> bands) => bands
			.SelectMany(band => new[] { Math.Log10(Math.Max(band.FrequencyHz, 1)), Math.Log10(Math.Max(band.Q, 1e-3)), band.GainDecibels })
			.ToArray();

		private static double[] Clamp(double[] values, double[] lower, double[] upper) =>
			values.Select((value, index) => Math.Clamp(value, lower[index], upper[index])).ToArray();

		private static IReadOnlyList<ParametricEqBand> Round(ParametricEqBand[] bands, IReadOnlyList<ParametricEqBandLimits> limits,
			double maximumGainDecibels) => bands.Select((band, index) => band with
			{
				FrequencyHz = Math.Clamp(Math.Round(band.FrequencyHz), Math.Ceiling(limits[index].MinimumHz), Math.Floor(limits[index].MaximumHz)),
				Q = Math.Clamp(Math.Round(band.Q, 2), limits[index].MinimumQ, limits[index].MaximumQ),
				GainDecibels = Math.Clamp(Math.Round(band.GainDecibels, 1), -maximumGainDecibels,
					Math.Min(maximumGainDecibels, limits[index].MaximumBoostDecibels))
			}).ToArray();
	}
}
