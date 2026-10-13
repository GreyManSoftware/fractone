using System.Text;

namespace Fractal.Ai
{
	public static class ToneDesignResearchQueryNormalizer
	{
		public const int MaximumQueryCharacters = 2000;

		public static ToneDesignResearchQuery Normalize(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				throw new ArgumentException("A research query is required.", nameof(value));
			}

			string normalized = value.Normalize(NormalizationForm.FormKC);
			StringBuilder canonical = new(normalized.Length);
			bool previousWasWhitespace = true;

			foreach (char character in normalized)
			{
				if (char.IsWhiteSpace(character))
				{
					if (!previousWasWhitespace)
					{
						canonical.Append(' ');
						previousWasWhitespace = true;
					}

					continue;
				}

				if (char.IsControl(character))
				{
					throw new ArgumentException(
						"Research queries must not contain control characters.",
						nameof(value));
				}

				if (IsUnicodeDash(character))
				{
					if (canonical.Length > 0 && canonical[^1] == ' ')
					{
						canonical.Length--;
					}

					canonical.Append(" - ");
					previousWasWhitespace = true;
					continue;
				}

				canonical.Append(NormalizePunctuation(character));
				previousWasWhitespace = false;
			}

			string canonicalText = canonical
				.ToString()
				.Trim()
				.ToLowerInvariant();

			if (canonicalText.Length == 0)
			{
				throw new ArgumentException("A research query is required.", nameof(value));
			}

			if (canonicalText.Length > MaximumQueryCharacters)
			{
				throw new ArgumentException(
					$"Research queries must not exceed {MaximumQueryCharacters:N0} canonical characters.",
					nameof(value));
			}

			return new ToneDesignResearchQuery(value, canonicalText);
		}

		private static char NormalizePunctuation(char value)
		{
			return value switch
			{
				'\u2018' or '\u2019' => '\'',
				'\u201c' or '\u201d' => '"',
				_ => value
			};
		}

		private static bool IsUnicodeDash(char value)
		{
			return value is '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2212';
		}
	}
}
