using System.Globalization;

namespace Fractal.Core
{
	public enum WetReferenceAnalysisStatus
	{
		RecommendationsAvailable,
		InsufficientSignal,
		InsufficientEvidence
	}

	public enum WetReferenceCorrectionArea
	{
		Equalization,
		GainStaging,
		CabinetVoicing,
		SignalChain
	}

	public sealed record WetReferenceCorrectionRecommendation(
		WetReferenceCorrectionArea Area,
		string SuggestedCorrection,
		string Evidence,
		string Limitation);

	public sealed record WetReferenceAnalysisRequest(
		AudioBuffer UserWetSignal,
		AudioBuffer TargetIsolatedGuitarTrack)
	{
		public void Validate()
		{
			ArgumentNullException.ThrowIfNull(UserWetSignal);
			ArgumentNullException.ThrowIfNull(TargetIsolatedGuitarTrack);
		}
	}

	public sealed record WetReferenceAnalysisResult(
		WetReferenceAnalysisStatus Status,
		string Summary,
		IReadOnlyList<WetReferenceCorrectionRecommendation> Recommendations,
		IReadOnlyList<string> Limitations)
	{
		public bool HasSufficientSignal => Status == WetReferenceAnalysisStatus.RecommendationsAvailable;
	}

	public sealed class WetReferenceAnalysisService
	{
		private const double RecommendationThresholdDecibels = 2;
		private const double MaximumRecommendedAdjustmentDecibels = 3;
		private const double MinimumRmsDecibels = -40;
		private readonly SpectralToneProfileAnalyzer _analyzer;

		public WetReferenceAnalysisService(SpectralToneProfileAnalyzer analyzer)
		{
			_analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
		}

		public WetReferenceAnalysisResult Analyze(WetReferenceAnalysisRequest request)
		{
			ArgumentNullException.ThrowIfNull(request);
			request.Validate();

			ToneProfile userProfile = _analyzer.Analyze(request.UserWetSignal);
			ToneProfile targetProfile = _analyzer.Analyze(request.TargetIsolatedGuitarTrack);
			IReadOnlyList<string> limitations =
			[
				"Comparing two musical clips supports bounded broad spectral and level recommendations only; it does not identify an amp, cabinet, microphone, pickup, playing technique, dynamics, phase, or time-domain response.",
				"Recordings should be comparable isolated guitar performances. Backing tracks, drums, bass, effects tails, clipping, and different performances can make the suggestions misleading.",
				"Target audio alone cannot create a valid physical impulse response. IR deconvolution requires a known excitation sweep and its controlled recorded response.",
				"Recommendations are reviewable manual starting points; this analysis does not change FM9 hardware or rewrite the generated SYX."
			];

			if (userProfile.RmsLevelDecibels < MinimumRmsDecibels ||
				targetProfile.RmsLevelDecibels < MinimumRmsDecibels)
			{
				return new WetReferenceAnalysisResult(
					WetReferenceAnalysisStatus.InsufficientSignal,
					"One or both recordings are too quiet for a reliable broad-band comparison. Re-record or export louder, unclipped isolated guitar audio.",
					[],
					limitations);
			}

			IReadOnlyList<ToneBandDelta> deltas = _analyzer.Compare(userProfile, targetProfile);
			List<WetReferenceCorrectionRecommendation> recommendations = CreateEqualizationRecommendations(deltas);
			AddGainStagingRecommendation(recommendations, userProfile, targetProfile);
			AddCabinetRecommendation(recommendations, deltas);
			recommendations.Add(new WetReferenceCorrectionRecommendation(
				WetReferenceCorrectionArea.SignalChain,
				"Make one change at a time: level-match, audition the cab or cab high-cut, then make small EQ corrections.",
				"The comparison is normalized within broad frequency bands, so it can show tonal imbalance but cannot assign a cause to one block.",
				"Treat this as an audition order, not a recovered signal chain or preset."));

			return new WetReferenceAnalysisResult(
				WetReferenceAnalysisStatus.RecommendationsAvailable,
				$"Compared broad spectral balance across {deltas.Count} guitar-relevant bands. Suggestions are conservative starting points, not a tone clone.",
				recommendations,
				limitations);
		}

		private static List<WetReferenceCorrectionRecommendation> CreateEqualizationRecommendations(
			IReadOnlyList<ToneBandDelta> deltas)
		{
			List<WetReferenceCorrectionRecommendation> recommendations = [];

			foreach (ToneBandDelta delta in deltas)
			{
				if (Math.Abs(delta.ReferenceMinusCurrentDecibels) < RecommendationThresholdDecibels)
				{
					continue;
				}

				double measuredDifference = Math.Round(delta.ReferenceMinusCurrentDecibels, 1);
				double adjustment = Math.Clamp(
					measuredDifference,
					-MaximumRecommendedAdjustmentDecibels,
					MaximumRecommendedAdjustmentDecibels);
				string direction = adjustment > 0 ? "Boost" : "Cut";
				string amount = Math.Abs(adjustment).ToString("0.0", CultureInfo.InvariantCulture);

				recommendations.Add(new WetReferenceCorrectionRecommendation(
					WetReferenceCorrectionArea.Equalization,
					$"{direction} the {delta.Band.Name.ToLowerInvariant()} region by approximately {amount} dB.",
					$"The measured target-minus-preset difference is {measuredDifference:+0.0;-0.0} dB from {delta.Band.LowerFrequencyHz:0} Hz to {delta.Band.UpperFrequencyHz:0} Hz.",
					"Use a small, level-matched EQ move and re-check; this is a broad-band estimate, not a fitted EQ curve."));
			}

			return recommendations;
		}

		private static void AddGainStagingRecommendation(
			ICollection<WetReferenceCorrectionRecommendation> recommendations,
			ToneProfile userProfile,
			ToneProfile targetProfile)
		{
			double difference = Math.Round(targetProfile.RmsLevelDecibels - userProfile.RmsLevelDecibels, 1);

			if (Math.Abs(difference) < 3)
			{
				return;
			}

			string direction = difference > 0 ? "raise" : "lower";
			recommendations.Add(new WetReferenceCorrectionRecommendation(
				WetReferenceCorrectionArea.GainStaging,
				$"Before changing distortion, level-match the user signal and target; the target recording is {Math.Abs(difference):0.0} dB {(difference > 0 ? "louder" : "quieter")}. If that persists after matching, {direction} output or block level rather than assuming an amp-gain change.",
				$"Measured RMS difference: {difference:+0.0;-0.0} dB.",
				"Recording level does not reveal amp gain, compression, pickup output, or performance dynamics."));
		}

		private static void AddCabinetRecommendation(
			ICollection<WetReferenceCorrectionRecommendation> recommendations,
			IReadOnlyList<ToneBandDelta> deltas)
		{
			double highFrequencyDelta = deltas
				.Where(delta => delta.Band.Name is "Presence" or "Treble")
				.Average(delta => delta.ReferenceMinusCurrentDecibels);

			if (Math.Abs(highFrequencyDelta) < RecommendationThresholdDecibels)
			{
				return;
			}

			string direction = highFrequencyDelta > 0
				? "audition a brighter cab or a less aggressive cab high-cut"
				: "audition a darker cab or a lower cab high-cut";

			recommendations.Add(new WetReferenceCorrectionRecommendation(
				WetReferenceCorrectionArea.CabinetVoicing,
				$"Before a large presence/treble EQ move, {direction}.",
				$"The averaged target-minus-user difference in the presence and treble bands is {highFrequencyDelta:+0.0;-0.0} dB.",
				"This cannot identify a cabinet, microphone, mic position, or IR; it only flags a high-frequency voicing mismatch."));
		}
	}
}
