using System.Text;
using System.Text.RegularExpressions;

namespace Fractal.Application
{
	public enum ToneDesignClarificationField
	{
		TargetIdentity,
		MusicalIntent
	}

	public enum ToneDesignClarificationStatus
	{
		Ready,
		NeedsClarification
	}

	public sealed record ToneDesignClarificationRequest(
		string ToneDescription,
		string PickupConfiguration,
		string OutputDevice)
	{
		public string? TargetIdentity { get; init; }

		public string? MusicalIntent { get; init; }
	}

	public sealed record ToneDesignClarificationQuestion(
		ToneDesignClarificationField Field,
		string Prompt,
		string HelpText,
		int MaximumCharacters);

	public sealed record ToneDesignClarificationResponse(
		ToneDesignClarificationStatus Status,
		IReadOnlyList<ToneDesignClarificationQuestion> Questions,
		string ResolvedToneDescription)
	{
		public bool RequiresClarification =>
			Status == ToneDesignClarificationStatus.NeedsClarification;
	}

	public sealed class ToneDesignClarificationService
	{
		public const int MaximumTargetIdentityCharacters = 160;
		public const int MaximumMusicalIntentCharacters = 240;
		// "the album" is unresolved only when no name follows it: "the album Master of Puppets", "the track One",
		// "the song called 'One'" and a possessive "the band's tone" already identify (or refer to) the target.
		private static readonly Regex UnresolvedTargetReference = new(
			@"\b(?:(?:this|that|the)\s+(?:artist|band|song|track|album|recording)(?!['’]s\b)" +
			// A name may follow an ellipsis ("the album ...And Justice for All") or be typed in lower case; a following
			// function word ("the song that I like", "the album and") still leaves the target unnamed.
			@"(?!\s+(?:(?:called|named|titled)\s+)?(?:(?:\.{2,}|…)\s*[\p{L}\p{N}]|[""'“‘]|(?!(?:that|which|who|i|it|its|we|they|you|is|was|are|and|or|but|with|from|by|for|to|please|where|when|in|on|of|a|an|the|sound|sounds|tone|tones|style|vibe|guitar|guitars|guitarist|rhythm|lead|solo)\b)[\p{L}\p{N}]))" +
			@"|(?:like|match|copy|recreate)\s+(?:it|them))\b",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		private static readonly Regex UnresolvedSectionReference = new(
			@"\b(?:(?:which|what|different|multiple|several|the)\s+)?(?:section|sections|scene|scenes|part|parts)\b",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		private static readonly HashSet<string> IntentTerms = new(StringComparer.OrdinalIgnoreCase)
		{
			"ambient",
			"bright",
			"clean",
			"crunch",
			"dark",
			"delay",
			"distorted",
			"distortion",
			"fuzz",
			"heavy",
			"intro",
			"lead",
			"rhythm",
			"solo",
			"tight",
			"verse",
			"chorus",
			"bridge",
			"warm"
		};
		private static readonly HashSet<string> NonDescriptiveTerms = new(StringComparer.OrdinalIgnoreCase)
		{
			"a",
			"am",
			"an",
			"and",
			"are",
			"build",
			"create",
			"d",
			"design",
			"dial",
			"fm9",
			"for",
			"fractal",
			"generate",
			"guitar",
			"i",
			"like",
			"looking",
			"m",
			"make",
			"me",
			"my",
			"need",
			"only",
			"please",
			"preset",
			"produce",
			"seeking",
			"the",
			"tone",
			"to",
			"want",
			"we",
			"would"
		};
		private static readonly HashSet<string> TargetReferenceTerms = new(StringComparer.OrdinalIgnoreCase)
		{
			"artist", "band", "song", "track", "album", "recording",
			"sound", "sounds", "sounding", "this", "that", "it", "them"
		};

		public ToneDesignClarificationResponse Evaluate(
			ToneDesignClarificationRequest request)
		{
			ArgumentNullException.ThrowIfNull(request);
			string description = RequireBounded(
				request.ToneDescription,
				2_000,
				nameof(request.ToneDescription));
			string? targetIdentity = OptionalBounded(
				request.TargetIdentity,
				MaximumTargetIdentityCharacters,
				nameof(request.TargetIdentity));
			string? musicalIntent = OptionalBounded(
				request.MusicalIntent,
				MaximumMusicalIntentCharacters,
				nameof(request.MusicalIntent));
			List<ToneDesignClarificationQuestion> questions = [];

			string[] descriptionTerms = Tokenize(description);
			bool missingTargetIdentity = targetIdentity is null &&
				(UnresolvedTargetReference.IsMatch(
					ToneDesignTextPolicy.MaskQuotedTargetNames(description, includeLabel: true)) ||
				(descriptionTerms.Any(TargetReferenceTerms.Contains) &&
					descriptionTerms.All(term =>
						NonDescriptiveTerms.Contains(term) || TargetReferenceTerms.Contains(term))));
			if (missingTargetIdentity)
			{
				questions.Add(new ToneDesignClarificationQuestion(
					ToneDesignClarificationField.TargetIdentity,
					"Which artist, track, or album is the tone target?",
					"Give only the missing target identity; an artist, track, album, or a clear combination is enough.",
					MaximumTargetIdentityCharacters));
			}

			if (musicalIntent is null && !missingTargetIdentity &&
				NeedsMusicalIntent(description, targetIdentity is not null))
			{
				questions.Add(new ToneDesignClarificationQuestion(
					ToneDesignClarificationField.MusicalIntent,
					"What should the preset or requested sections/scenes achieve musically?",
					"For example: tight rhythm, clean verse and lead chorus, or an ambient intro.",
					MaximumMusicalIntentCharacters));
			}

			string resolvedDescription = BuildResolvedDescription(
				description,
				targetIdentity,
				musicalIntent);
			return new ToneDesignClarificationResponse(
				questions.Count == 0
					? ToneDesignClarificationStatus.Ready
					: ToneDesignClarificationStatus.NeedsClarification,
				questions,
				resolvedDescription);
		}

		private static bool NeedsMusicalIntent(string description, bool hasSuppliedTargetIdentity)
		{
			description = ToneDesignTextPolicy.MaskQuotedTargetNames(description);
			string[] terms = Tokenize(description);
			if (terms.Any(IntentTerms.Contains))
			{
				return false;
			}

			if (UnresolvedSectionReference.IsMatch(description) &&
				terms.Any(term => term is "create" or "make" or "build" or "design" or
					"generate" or "produce" or "want" or "need"))
			{
				return true;
			}

			if (hasSuppliedTargetIdentity)
			{
				return false;
			}

			string[] descriptiveTerms = terms
				.Where(term => !NonDescriptiveTerms.Contains(term))
				.ToArray();
			return descriptiveTerms.Length == 0;
		}

		private static string BuildResolvedDescription(
			string description,
			string? targetIdentity,
			string? musicalIntent)
		{
			List<string> parts = [description];
			if (targetIdentity is not null)
			{
				parts.Add($"Clarified tone target: {targetIdentity}");
			}

			if (musicalIntent is not null)
			{
				parts.Add($"Clarified musical intent: {musicalIntent}");
			}

			return string.Join(Environment.NewLine, parts);
		}

		private static string RequireBounded(
			string? value,
			int maximumCharacters,
			string parameterName)
		{
			return OptionalBounded(value, maximumCharacters, parameterName) ??
				throw new ArgumentException("A tone description is required.", parameterName);
		}

		private static string? OptionalBounded(
			string? value,
			int maximumCharacters,
			string parameterName)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return null;
			}

			string trimmed = value.Trim();
			if (trimmed.Length > maximumCharacters)
			{
				throw new ArgumentException(
					$"{parameterName} must be {maximumCharacters:N0} characters or fewer.",
					parameterName);
			}

			if (trimmed.Any(character =>
				char.IsControl(character) &&
				character is not '\r' and not '\n' and not '\t'))
			{
				throw new ArgumentException(
					$"{parameterName} contains unsupported control characters.",
					parameterName);
			}

			return trimmed.Normalize(NormalizationForm.FormKC);
		}

		private static string[] Tokenize(string value)
		{
			return ToneDesignTextPolicy.Tokenize(value);
		}
	}
}
