using Fractal.Application;
using System.Diagnostics;

namespace Fractal.Ai
{
	public sealed class ToneDesignEvidenceRetriever
	{
		private readonly IToneDesignEvidenceProvider _provider;
		private readonly IToneDesignEvidenceSourcePolicy _sourcePolicy;
		private readonly TimeProvider _timeProvider;

		public ToneDesignEvidenceRetriever(
			IToneDesignEvidenceProvider provider,
			IToneDesignEvidenceSourcePolicy sourcePolicy,
			TimeProvider? timeProvider = null)
		{
			_provider = provider;
			_sourcePolicy = sourcePolicy;
			_timeProvider = timeProvider ?? TimeProvider.System;
		}

		public static ToneDesignEvidenceRetriever Disabled { get; } = new(
			new UnavailableToneDesignEvidenceProvider(),
			new ToneDesignEvidenceSourcePolicy([]));

		public async Task<ToneDesignEvidencePromptContext> EnrichAsync(
			ToneDesignChatRequest request,
			CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(request);
			if (request.Refinement is not null)
			{
				IReadOnlyList<ToneDesignEvidenceCitationBinding> bindings =
					request.Refinement.OriginalProposal.Evidence
						.Where(evidence => !string.IsNullOrWhiteSpace(evidence.SourceUrl))
						.Select(evidence => new ToneDesignEvidenceCitationBinding(
							evidence.SourceUrl,
							evidence.SourceUrl))
						.Distinct()
						.ToArray();
				return new ToneDesignEvidencePromptContext(
					request.GetPromptWithDeviceProfile(),
					bindings);
			}

			long started = Stopwatch.GetTimestamp();
			ToneDesignResearchQuery query =
				ToneDesignResearchQueryNormalizer.Normalize(request.ResearchQueryText ?? request.UserPrompt);
			List<string> localGuidance =
			[
				"Device-contract evidence: the downloadable FM9 Ride baseline fixes the block roster, instances, positions, parameter ranges, routes, and scene limits stated in the system prompt.",
				"Signal-processing evidence: gain, filtering, dynamics, and time effects interact; choose values that serve the target sound and recommend audition rather than claiming a matched sound."
			];
			string prompt = request.UserPrompt;

			if (ContainsAny(prompt, "tight", "mud", "low end", "bass"))
			{
				localGuidance.Add(
					"General design heuristic (not live research): excessive low-frequency energy entering nonlinear gain can reduce articulation; prefer bounded low-cut, gain, and post-EQ adjustments, evaluated by audition.");
			}

			if (ContainsAny(prompt, "bright", "harsh", "fizz", "presence"))
			{
				localGuidance.Add(
					"General design heuristic (not live research): high-cut, treble, presence, and cabinet choice affect different parts of perceived brightness; change one bounded control at a time.");
			}

			if (ContainsAny(prompt, "lead", "solo", "scene"))
			{
				localGuidance.Add(
					"General design heuristic (not live research): use scene channel/bypass changes only when they serve an explicit musical purpose; do not invent blocks or unsupported level controls.");
			}

			ToneDesignEvidenceRetrievalResult retrieval =
				await _provider.RetrieveAsync(query, cancellationToken);
			ToneDesignTelemetry.RecordEvidence(
				retrieval,
				Stopwatch.GetElapsedTime(started));
			List<ToneDesignEvidenceRecord> records = [];
			foreach (ToneDesignEvidenceSourceCandidate candidate in retrieval.Sources
				.Take(ToneDesignEvidencePromptContextBuilder.MaximumEvidenceRecords))
			{
				try
				{
					records.Add(ToneDesignEvidenceRecord.Create(
						candidate,
						_sourcePolicy,
						_timeProvider.GetUtcNow()));
				}
				catch (InvalidOperationException)
				{
					// Provider output is untrusted and denied sources are omitted.
				}
			}

			string detail = retrieval.Status switch
			{
				_ when retrieval.Sources.Count > 0 && records.Count == 0 =>
					"No live evidence passed the configured source policy.",
				ToneDesignEvidenceRetrievalStatus.Unavailable =>
					$"Live evidence unavailable: {retrieval.Detail}",
				ToneDesignEvidenceRetrievalStatus.NoResults =>
					$"No live evidence matched the configured source policy: {retrieval.Detail}",
				ToneDesignEvidenceRetrievalStatus.LimitReached =>
					$"Live evidence request limit reached: {retrieval.Detail}",
				_ => retrieval.Detail
			};

			return ToneDesignEvidencePromptContextBuilder.Create(
				request,
				query,
				localGuidance,
				records,
				records.Count == 0 ? detail : null);
		}

		private static bool ContainsAny(string value, params string[] terms)
		{
			return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
		}
	}

	public sealed class UnavailableToneDesignEvidenceProvider : IToneDesignEvidenceProvider
	{
		public Task<ToneDesignEvidenceRetrievalResult> RetrieveAsync(
			ToneDesignResearchQuery query,
			CancellationToken cancellationToken)
		{
			return Task.FromResult(new ToneDesignEvidenceRetrievalResult(
				ToneDesignEvidenceRetrievalStatus.Unavailable,
				"no live evidence provider is configured",
				[]));
		}
	}
}
