using System.Globalization;

namespace Fractal.Core
{
	public sealed class SpectralToneMatchService
	{
		private readonly SpectralToneProfileAnalyzer _analyzer;

		public SpectralToneMatchService(SpectralToneProfileAnalyzer analyzer)
		{
			_analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
		}

		public ToneAnalysisResult Analyze(
			AudioBuffer currentTone,
			AudioBuffer referenceTone,
			double recommendationThresholdDecibels = 2)
		{
			if (recommendationThresholdDecibels < 0)
			{
				throw new ArgumentOutOfRangeException(
					nameof(recommendationThresholdDecibels),
					"The recommendation threshold cannot be negative.");
			}

			ToneProfile currentProfile = _analyzer.Analyze(currentTone);
			ToneProfile referenceProfile = _analyzer.Analyze(referenceTone);
			IReadOnlyList<ToneBandDelta> deltas = _analyzer.Compare(currentProfile, referenceProfile);
			List<ToneRecommendation> recommendations = [];

			foreach (ToneBandDelta delta in deltas)
			{
				if (Math.Abs(delta.ReferenceMinusCurrentDecibels) < recommendationThresholdDecibels)
				{
					continue;
				}

				double adjustment = Math.Round(delta.ReferenceMinusCurrentDecibels, 1);
				string direction = adjustment > 0 ? "Boost" : "Cut";
				string amount = Math.Abs(adjustment).ToString("0.0", CultureInfo.InvariantCulture);

				recommendations.Add(new ToneRecommendation(
					delta.Band.Name,
					$"{direction} the {delta.Band.Name.ToLowerInvariant()} region by approximately {amount} dB.",
					$"The reference has {adjustment:+0.0;-0.0} dB more relative energy from {delta.Band.LowerFrequencyHz:0} Hz to {delta.Band.UpperFrequencyHz:0} Hz."));
			}

			return new ToneAnalysisResult(
				ToneAnalysisKind.SpectralToneMatch,
				$"Compared {deltas.Count} frequency bands without treating either recording as a physical impulse response.",
				recommendations);
		}
	}
}
