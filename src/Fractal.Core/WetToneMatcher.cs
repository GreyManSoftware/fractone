using System.Numerics;

namespace Fractal.Core
{
	public enum WetToneMatchStatus
	{
		Matched,
		InsufficientSignal,
		ClippedInput,
		InsufficientSpectralOverlap,
		UnreliableFit
	}

	public sealed record ToneMatchEqBand(double FrequencyHz, double Q, double GainDecibels);
	public sealed record ToneMatchSpectrumPoint(
		double FrequencyHz, double TargetMinusUserDecibels, double AppliedEqDecibels);
	public sealed record ToneMatchRecordedSpectrumPoint(
		double FrequencyHz, double UserRelativeDecibels, double TargetRelativeDecibels, double PreviewRelativeDecibels);

	public sealed record WetToneMatchResult(
		WetToneMatchStatus Status,
		string Summary,
		double UserActiveRmsDecibels,
		double TargetActiveRmsDecibels,
		double? PreviewActiveRmsDecibels,
		double? BeforeSpectralErrorDecibels,
		double? AfterSpectralErrorDecibels,
		double AppliedLevelGainDecibels,
		double HeadroomReductionDecibels,
		IReadOnlyList<ToneMatchEqBand> EqBands,
		IReadOnlyList<ToneMatchSpectrumPoint> Spectrum,
		IReadOnlyList<string> Warnings,
		MultichannelAudioBuffer? Preview)
	{
		public bool HasPreview => Status == WetToneMatchStatus.Matched && Preview is not null;
		public IReadOnlyList<ToneMatchRecordedSpectrumPoint> RecordedSpectrum { get; init; } = [];
		// Set by a Parametric EQ match: the fitted bands, the EQ the recording was made through, and the bleed guard.
		public IReadOnlyList<ParametricEqBand> ParametricEq { get; init; } = [];
		public IReadOnlyList<ParametricEqBand> RecordedEq { get; init; } = [];
		public double? GuardHz { get; init; }
	}

	public sealed record ParametricEqMatchSettings(
		IReadOnlyList<ParametricEqBand> RecordedEq,
		IReadOnlyList<ParametricEqBandLimits> Limits,
		double MaximumGainDecibels)
	{
		public const double DefaultGuardHz = 90;
		public double GuardHz { get; init; } = DefaultGuardHz;
		// FM9 processing runs at 48 kHz; the fitted settings are what the device stores, whatever the recording's rate.
		public int DeviceSampleRate { get; init; } = 48_000;
	}

	public sealed class WetToneMatcher
	{
		public const double MaximumDurationSeconds = 60;
		public const double MaximumEqGainDecibels = 6;
		public const double MaximumLevelGainDecibels = 12;
		public const double PreviewPeakCeilingDecibels = -1;
		private const int MaximumFrames = 256;
		private const double EqQ = 0.85;
		private static readonly double[] Centers = [100, 300, 900, 2700, 6500];

		public WetToneMatchResult Match(MultichannelAudioBuffer user, MultichannelAudioBuffer target)
		{
			Validate(user);
			Validate(target);
			double upperFrequency = Math.Min(12000, Math.Min(user.SampleRate, target.SampleRate) * 0.45);
			double[] frequencies = Enumerable.Range(0, 64)
				.Select(index => 50 * Math.Pow(2, index / 6.0))
				.TakeWhile(frequency => frequency <= upperFrequency).ToArray();
			Profile current = Analyze(user, frequencies);
			Profile reference = Analyze(target, frequencies);
			if (Peak(user) >= 0.9995 || Peak(target) >= 0.9995)
			{
				return Refuse(WetToneMatchStatus.ClippedInput,
					"One recording is clipped or too close to full scale. Export an unclipped recording before matching.",
					current, reference);
			}
			if (current.RmsDecibels < -50 || reference.RmsDecibels < -50)
			{
				return Refuse(WetToneMatchStatus.InsufficientSignal,
					"One recording has insufficient active guitar signal. Silence and noise cannot establish an EQ match.",
					current, reference);
			}

			double currentFloor = current.Powers.Max() * 0.00001;
			double referenceFloor = reference.Powers.Max() * 0.00001;
			int[] usable = Enumerable.Range(0, frequencies.Length)
				.Where(index => current.Powers[index] > currentFloor && reference.Powers[index] > referenceFloor)
				.ToArray();
			if (usable.Length < 12 || frequencies[usable[^1]] / frequencies[usable[0]] < 8)
			{
				return Refuse(WetToneMatchStatus.InsufficientSpectralOverlap,
					"The clips do not share enough guitar-frequency energy for a reliable five-band fit. Use comparable musical passages, not a single note or unrelated performances.",
					current, reference);
			}
			double[] fittingFrequencies = usable.Select(index => frequencies[index]).ToArray();
			double[] differences = Differences(current, reference, usable);
			double beforeError = Error(differences);
			double[] gains = beforeError < 0.15 ? new double[Centers.Length] :
				Fit(fittingFrequencies, differences, user.SampleRate);
			BoundResponse(gains, user.SampleRate);
			ToneMatchEqBand[] bands = Centers.Select((frequency, index) =>
				new ToneMatchEqBand(frequency, EqQ, gains[index])).ToArray();
			MultichannelAudioBuffer filtered = Filter(user, bands);
			Profile filteredProfile = Analyze(filtered, frequencies);
			double afterError = Error(Differences(filteredProfile, reference, usable));
			if (afterError > beforeError + 0.05)
			{
				return Refuse(WetToneMatchStatus.UnreliableFit,
					"The fitted EQ did not improve the measured comparison. No preview was produced; use a more comparable isolated passage.",
					current, reference);
			}

			(MultichannelAudioBuffer preview, double appliedLevel, double headroomReduction) = LevelPreview(
				filtered, filteredProfile, reference,
				"Apply the fitted EQ after the existing Cab response. No FM9, SYX, or IR is changed by this preview.",
				out List<string> warnings);
			double[] applied = Response(bands, fittingFrequencies, user.SampleRate);
			Profile previewProfile = Analyze(preview, frequencies);
			return new WetToneMatchResult(WetToneMatchStatus.Matched,
				"Fitted five bounded peaking-EQ bands from active, stereo-aware spectra and rendered a level-adjusted preview.",
				current.RmsDecibels, reference.RmsDecibels, previewProfile.RmsDecibels,
				beforeError, afterError, appliedLevel, headroomReduction, bands,
				fittingFrequencies.Select((frequency, index) =>
					new ToneMatchSpectrumPoint(frequency, differences[index], applied[index])).ToArray(),
				warnings, preview)
			{
				RecordedSpectrum = frequencies.Select((frequency, index) => new ToneMatchRecordedSpectrumPoint(
					frequency, RelativePower(current, index), RelativePower(reference, index),
					RelativePower(previewProfile, index))).ToArray()
			};
		}

		// Fits a device Parametric EQ instead of the fixed bands. The recording was made through RecordedEq, so the fit
		// starts from it and aims at RecordedEq plus the measured difference; below GuardHz (where isolated stems often
		// carry bass bleed) it keeps the recorded response. The preview removes RecordedEq and applies the fitted EQ.
		public WetToneMatchResult MatchParametric(MultichannelAudioBuffer user, MultichannelAudioBuffer target,
			ParametricEqMatchSettings settings)
		{
			ArgumentNullException.ThrowIfNull(settings);
			Validate(user);
			Validate(target);
			double upperFrequency = Math.Min(12000, Math.Min(user.SampleRate, target.SampleRate) * 0.45);
			double[] frequencies = Enumerable.Range(0, 64)
				.Select(index => 50 * Math.Pow(2, index / 6.0))
				.TakeWhile(frequency => frequency <= upperFrequency).ToArray();
			Profile current = Analyze(user, frequencies);
			Profile reference = Analyze(target, frequencies);
			if (Peak(user) >= 0.9995 || Peak(target) >= 0.9995)
			{
				return Refuse(WetToneMatchStatus.ClippedInput,
					"One recording is clipped or too close to full scale. Export an unclipped recording (lower the preset or render level) before matching.",
					current, reference);
			}
			if (current.RmsDecibels < -50 || reference.RmsDecibels < -50)
			{
				return Refuse(WetToneMatchStatus.InsufficientSignal,
					"One recording has insufficient active guitar signal. Silence and noise cannot establish an EQ match.",
					current, reference);
			}
			double currentFloor = current.Powers.Max() * 0.00001;
			double referenceFloor = reference.Powers.Max() * 0.00001;
			int[] usable = Enumerable.Range(0, frequencies.Length)
				.Where(index => current.Powers[index] > currentFloor && reference.Powers[index] > referenceFloor)
				.ToArray();
			int[] guarded = usable.Where(index => frequencies[index] >= settings.GuardHz).ToArray();
			if (guarded.Length < 12 || frequencies[guarded[^1]] / frequencies[guarded[0]] < 8)
			{
				return Refuse(WetToneMatchStatus.InsufficientSpectralOverlap,
					"The clips do not share enough guitar-frequency energy for a reliable EQ fit. Use comparable musical passages, not a single note or unrelated performances.",
					current, reference);
			}

			double[] fittingFrequencies = usable.Select(index => frequencies[index]).ToArray();
			double[] differences = Differences(current, reference, usable);
			double[] recorded = ParametricEqResponse.Decibels(settings.RecordedEq, fittingFrequencies, settings.DeviceSampleRate);
			double[] wanted = fittingFrequencies.Select((frequency, index) =>
				recorded[index] + (frequency >= settings.GuardHz ? differences[index] : 0)).ToArray();
			IReadOnlyList<ParametricEqBand> fitted = ParametricEqFitter.Fit(fittingFrequencies, wanted, settings.Limits,
				settings.RecordedEq, settings.MaximumGainDecibels, settings.DeviceSampleRate,
				fittingFrequencies.Select(StemTrust).ToArray());

			MultichannelAudioBuffer filtered = ParametricEqResponse.Apply(user,
				[.. settings.RecordedEq.Select(ParametricEqResponse.Inverse), .. fitted]);
			Profile filteredProfile = Analyze(filtered, frequencies);
			double beforeError = Error(Recentre(differences, fittingFrequencies, settings.GuardHz));
			double afterError = Error(Recentre(Differences(filteredProfile, reference, usable), fittingFrequencies, settings.GuardHz));
			if (afterError > beforeError + 0.05)
			{
				return Refuse(WetToneMatchStatus.UnreliableFit,
					"The fitted EQ did not improve the measured comparison. No preview was produced; use a more comparable isolated passage.",
					current, reference);
			}

			(MultichannelAudioBuffer preview, double appliedLevel, double headroomReduction) = LevelPreview(
				filtered, filteredProfile, reference,
				"The preview swaps the recording's Parametric EQ for the fitted one. Nothing on your FM9 changes until you apply it to the preset and load that.",
				out List<string> warnings);
			warnings.Add($"Below {settings.GuardHz:0} Hz the recording's existing EQ is kept: isolated stems often carry bass or kick bleed there.");
			double[] fittedResponse = ParametricEqResponse.Decibels(fitted, fittingFrequencies, settings.DeviceSampleRate);
			Profile previewProfile = Analyze(preview, frequencies);
			return new WetToneMatchResult(WetToneMatchStatus.Matched,
				"Fitted the Parametric EQ's five bands (frequency, Q and gain) from active, stereo-aware spectra and rendered a level-adjusted preview.",
				current.RmsDecibels, reference.RmsDecibels, previewProfile.RmsDecibels,
				beforeError, afterError, appliedLevel, headroomReduction, [],
				fittingFrequencies.Select((frequency, index) =>
					new ToneMatchSpectrumPoint(frequency, differences[index], fittedResponse[index] - recorded[index])).ToArray(),
				warnings, preview)
			{
				RecordedSpectrum = frequencies.Select((frequency, index) => new ToneMatchRecordedSpectrumPoint(
					frequency, RelativePower(current, index), RelativePower(reference, index),
					RelativePower(previewProfile, index))).ToArray(),
				ParametricEq = fitted,
				RecordedEq = settings.RecordedEq,
				GuardHz = settings.GuardHz
			};
		}

		// The active-passage power spectrum (dB) at each frequency, averaged over a band halfBandOctaves either side: the
		// analysis the matcher itself uses, for callers that compare recordings with other measured responses.
		public static double[] ActiveSpectrumDecibels(MultichannelAudioBuffer audio, IReadOnlyList<double> frequencies, double halfBandOctaves)
		{
			Validate(audio);
			return Analyze(audio, frequencies.ToArray(), halfBandOctaves).Powers.Select(power => 10 * Math.Log10(Math.Max(power, 1e-30))).ToArray();
		}

		// How far an isolated stem's spectrum is trusted at a frequency: fully up to 4.5 kHz, then less and less (a tenth
		// from 9 kHz), where stem separation and the record's mastering leave a top end brighter than any amp and cab.
		// Fits that chased it boosted 6-10 kHz by 4-9 dB, which players heard as fizz.
		public static double StemTrust(double frequencyHz) =>
			frequencyHz <= 4500 ? 1 : Math.Max(0.1, 1 - 0.9 * Math.Log2(frequencyHz / 4500));

		// Level-matches a filtered take to the reference within ±12 dB, keeping its peaks under the preview ceiling.
		private static (MultichannelAudioBuffer Preview, double AppliedLevel, double HeadroomReduction) LevelPreview(
			MultichannelAudioBuffer filtered, Profile filteredProfile, Profile reference, string applyNote, out List<string> warnings)
		{
			double requestedLevel = reference.RmsDecibels - filteredProfile.RmsDecibels;
			double appliedLevel = Math.Clamp(requestedLevel, -MaximumLevelGainDecibels, MaximumLevelGainDecibels);
			double peakAfterGain = Peak(filtered) * Math.Pow(10, appliedLevel / 20);
			double ceiling = Math.Pow(10, PreviewPeakCeilingDecibels / 20);
			double headroomReduction = peakAfterGain > ceiling ? 20 * Math.Log10(peakAfterGain / ceiling) : 0;
			appliedLevel -= headroomReduction;
			float multiplier = (float)Math.Pow(10, appliedLevel / 20);
			MultichannelAudioBuffer preview = new(filtered.SampleRate, filtered.Channels
				.Select(channel => (IReadOnlyList<float>)channel.Select(sample => sample * multiplier).ToArray()).ToArray());
			warnings =
			[
				"This is a statistical spectral match, not recovered artist settings or a physical cabinet IR.",
				"Different notes, pickups, playing dynamics, distortion, double-tracking, ambience, or stem-separation artifacts can confound the comparison.",
				applyNote,
				"Active RMS is a level estimate, not a perceptual loudness guarantee. Audition at matched monitor volume."
			];
			if (Math.Abs(requestedLevel) > MaximumLevelGainDecibels)
			{
				warnings.Add("The requested level correction exceeded 12 dB and was bounded.");
			}
			if (headroomReduction > 0)
			{
				warnings.Add($"Preview level was reduced by {headroomReduction:F2} dB to preserve the -1 dBFS sample-peak ceiling; exact reference level was not reached.");
			}
			return (preview, appliedLevel, headroomReduction);
		}

		// Differences re-levelled over the guarded range only, so bleed below the guard does not skew the error.
		private static double[] Recentre(double[] differences, double[] frequencies, double guardHz)
		{
			double[] guarded = differences.Where((_, index) => frequencies[index] >= guardHz).ToArray();
			double mean = guarded.Average();
			return guarded.Select(value => value - mean).ToArray();
		}

		private static double RelativePower(Profile profile, int index) =>
			10 * Math.Log10(Math.Max(profile.Powers[index], 1e-30)) - profile.RmsDecibels;

		private static void Validate(MultichannelAudioBuffer audio)
		{
			ArgumentNullException.ThrowIfNull(audio);
			audio.Validate();
			double seconds = (double)audio.Channels[0].Count / audio.SampleRate;
			if (audio.SampleRate is < 16000 or > 96000 || seconds is < 1 or > MaximumDurationSeconds)
			{
				throw new ArgumentException("Use 1-60 seconds of mono or stereo audio at 16-96 kHz.", nameof(audio));
			}
		}

		private sealed record Profile(double RmsDecibels, double[] Powers);

		private static Profile Analyze(MultichannelAudioBuffer audio, double[] frequencies, double halfBandOctaves = 1.0 / 6)
		{
			int size = 2048;
			while (size < audio.SampleRate / 6 && size < 16384)
			{
				size *= 2;
			}
			int available = 1 + (audio.Channels[0].Count - size) / (size / 2);
			int count = Math.Min(MaximumFrames, available);
			int[] starts = Enumerable.Range(0, count).Select(index =>
				(count == 1 ? 0 : (int)((long)index * (available - 1) / (count - 1))) * (size / 2)).ToArray();
			double[] energies = starts.Select(start => audio.Channels.Sum(channel =>
				Enumerable.Range(start, size).Sum(index => (double)channel[index] * channel[index])) /
				(size * audio.Channels.Count)).ToArray();
			double maximumEnergy = energies.Max();
			if (maximumEnergy <= 1e-20)
			{
				return new Profile(-200, new double[frequencies.Length]);
			}
			double[] window = Enumerable.Range(0, size)
				.Select(index => 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (size - 1))).ToArray();
			double windowEnergy = window.Sum(value => value * value);
			double[] density = new double[size / 2 + 1];
			int activeCount = 0;
			double activeEnergy = 0;
			for (int frame = 0; frame < count; frame++)
			{
				if (energies[frame] < maximumEnergy * 0.001)
				{
					continue;
				}
				activeCount++;
				activeEnergy += energies[frame];
				foreach (IReadOnlyList<float> channel in audio.Channels)
				{
					Complex[] spectrum = new Complex[size];
					for (int index = 0; index < size; index++)
					{
						spectrum[index] = new Complex(channel[starts[frame] + index] * window[index], 0);
					}
					FastFourierTransform.TransformInPlace(spectrum);
					for (int bin = 1; bin < density.Length; bin++)
					{
						density[bin] += spectrum[bin].Magnitude * spectrum[bin].Magnitude /
							(windowEnergy * audio.SampleRate);
					}
				}
			}
			double[] powers = frequencies.Select(frequency =>
			{
				int first = Math.Max(1, (int)Math.Ceiling(frequency / Math.Pow(2, halfBandOctaves) * size / audio.SampleRate));
				int last = Math.Min(density.Length - 1, (int)Math.Floor(frequency * Math.Pow(2, halfBandOctaves) * size / audio.SampleRate));
				double sum = 0;
				for (int bin = first; bin <= last; bin++)
				{
					sum += density[bin];
				}
				return last >= first ? sum / ((last - first + 1) * activeCount * audio.Channels.Count) : 0;
			}).ToArray();
			return new Profile(10 * Math.Log10(activeEnergy / activeCount), powers);
		}

		private static double[] Differences(Profile user, Profile target, int[] usable)
		{
			double[] differences = usable.Select(index =>
				10 * Math.Log10(Math.Max(target.Powers[index], 1e-30) /
					Math.Max(user.Powers[index], 1e-30))).ToArray();
			double mean = differences.Average();
			return differences.Select(value => value - mean).ToArray();
		}

		private static double Error(double[] differences) =>
			Math.Sqrt(differences.Average(value => value * value));

		private static double[] Fit(double[] frequencies, double[] desired, int sampleRate)
		{
			double[] gains = new double[Centers.Length];
			foreach (double step in new[] { 1.0, 0.5, 0.25 })
			{
				for (int pass = 0; pass < 12; pass++)
				{
					bool changed = false;
					for (int band = 0; band < gains.Length; band++)
					{
						double original = gains[band];
						double best = Score(gains, frequencies, desired, sampleRate);
						double selected = original;
						foreach (double candidate in new[] { original - step, original + step })
						{
							gains[band] = Math.Clamp(candidate, -MaximumEqGainDecibels, MaximumEqGainDecibels);
							double score = Score(gains, frequencies, desired, sampleRate);
							if (score < best)
							{
								best = score;
								selected = gains[band];
							}
						}
						gains[band] = selected;
						changed |= selected != original;
					}
					if (!changed)
					{
						break;
					}
				}
			}
			return gains;
		}

		private static double Score(double[] gains, double[] frequencies, double[] desired, int sampleRate)
		{
			double[] response = Response(Centers.Select((frequency, index) =>
				new ToneMatchEqBand(frequency, EqQ, gains[index])).ToArray(), frequencies, sampleRate);
			double mean = response.Average();
			return desired.Select((value, index) => Math.Pow(value - response[index] + mean, 2)).Average() +
				0.01 * gains.Sum(value => value * value);
		}

		private static void BoundResponse(double[] gains, int sampleRate)
		{
			double[] frequencies = Enumerable.Range(0, 1024)
				.Select(index => 20 * Math.Pow(sampleRate * 0.499 / 20, index / 1023.0)).ToArray();
			for (int attempt = 0; attempt < 4; attempt++)
			{
				double maximum = Response(Centers.Select((frequency, index) =>
					new ToneMatchEqBand(frequency, EqQ, gains[index])).ToArray(), frequencies, sampleRate)
					.Max(value => Math.Abs(value));
				if (maximum <= MaximumEqGainDecibels)
				{
					return;
				}
				for (int index = 0; index < gains.Length; index++)
				{
					gains[index] *= MaximumEqGainDecibels / maximum * 0.999;
				}
			}
			throw new InvalidOperationException("Unable to bound the fitted EQ response safely.");
		}

		private sealed record Coefficients(double B0, double B1, double B2, double A1, double A2);

		private static Coefficients CoefficientsFor(ToneMatchEqBand band, int sampleRate)
		{
			double amplitude = Math.Pow(10, band.GainDecibels / 40);
			double omega = 2 * Math.PI * band.FrequencyHz / sampleRate;
			double alpha = Math.Sin(omega) / (2 * band.Q);
			double a0 = 1 + alpha / amplitude;
			return new Coefficients((1 + alpha * amplitude) / a0, -2 * Math.Cos(omega) / a0,
				(1 - alpha * amplitude) / a0, -2 * Math.Cos(omega) / a0, (1 - alpha / amplitude) / a0);
		}

		private static double[] Response(IReadOnlyList<ToneMatchEqBand> bands, double[] frequencies, int sampleRate)
		{
			Coefficients[] filters = bands.Select(band => CoefficientsFor(band, sampleRate)).ToArray();
			return frequencies.Select(frequency =>
			{
				Complex delay = Complex.FromPolarCoordinates(1, -2 * Math.PI * frequency / sampleRate);
				return filters.Sum(filter => 20 * Math.Log10(((filter.B0 + filter.B1 * delay + filter.B2 * delay * delay) /
					(1 + filter.A1 * delay + filter.A2 * delay * delay)).Magnitude));
			}).ToArray();
		}

		private static MultichannelAudioBuffer Filter(MultichannelAudioBuffer source, IReadOnlyList<ToneMatchEqBand> bands)
		{
			float[][] channels = source.Channels.Select(channel => channel.ToArray()).ToArray();
			foreach (float[] channel in channels)
			{
				foreach (ToneMatchEqBand band in bands)
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

		private static double Peak(MultichannelAudioBuffer audio) =>
			audio.Channels.Max(channel => channel.Max(sample => Math.Abs((double)sample)));

		private static WetToneMatchResult Refuse(WetToneMatchStatus status, string summary, Profile user, Profile target) =>
			new(status, summary, user.RmsDecibels, target.RmsDecibels, null, null, null, 0, 0, [], [],
				["No fitted correction or audio preview was produced. This workflow does not change FM9 hardware."], null);
	}
}
