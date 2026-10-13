using System.Text;
using System.Text.RegularExpressions;

namespace Fractal.Application
{
	// Preset names as the FM9 stores them: up to 31 printable ASCII characters.
	public static class Fm9PresetName
	{
		public const int MaximumLength = 31;
		// Long enough to tell presets apart in a list when they have no name of their own.
		public const int MaximumLabelLength = 60;

		// A valid FM9 name from any text: accents folded, other characters dropped, cut at a word. Null when nothing is left.
		public static string? Normalize(string? name) => Shorten(Clean(name), MaximumLength);

		// A download file name: the preset's name without characters Windows refuses, or the fallback.
		public static string FileName(string? name, string fallback)
		{
			string replaced = new((Normalize(name) ?? string.Empty)
				.Select(character => "<>:\"/\\|?*".Contains(character) ? ' ' : character).ToArray());
			string safe = string.Join(' ', replaced.Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
			return safe.Length == 0 ? fallback : safe;
		}

		// The label jobs were given before names were suggested; kept so those can be recognised and replaced.
		public static string? LegacyLabel(string? text) => Shorten(Clean(text?.Split('\n')[0]), MaximumLabelLength);

		// A short name from a prompt: a quoted title when there is one (the song "Ride the Lightning"), otherwise the first
		// line without its request wording ("Make me a tone like ..."), cut to an FM9 name.
		public static string? Suggest(string? prompt)
		{
			string line = Clean(prompt?.Split('\n')[0]);
			Match quoted = Regex.Match(line, "\"([^\"]{2,})\"");
			if (quoted.Success && Normalize(quoted.Groups[1].Value) is { } title)
				return title;
			string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			int start = 0;
			while (start < words.Length - 1 && RequestWords.Contains(words[start].TrimEnd(',', ':')))
				start++;
			string rest = string.Join(' ', words[start..]);
			if (Normalize(rest.Length > 0 ? char.ToUpperInvariant(rest[0]) + rest[1..] : rest) is not { } name)
				return null;
			// Cutting at a word can leave "... tone for"; a name reads better without the dangling joiner.
			List<string> kept = [.. name.Split(' ')];
			while (kept.Count > 1 && TrailingWords.Contains(kept[^1]))
				kept.RemoveAt(kept.Count - 1);
			return string.Join(' ', kept);
		}

		private static readonly HashSet<string> TrailingWords = new(StringComparer.OrdinalIgnoreCase)
		{
			"a", "an", "the", "for", "of", "on", "by", "with", "and", "to", "from", "in", "like"
		};

		private static readonly HashSet<string> RequestWords = new(StringComparer.OrdinalIgnoreCase)
		{
			"please", "can", "could", "would", "you", "i", "i'd", "id", "i'm", "want", "need", "like", "love", "looking", "produce",
			"make", "create", "give", "build", "design", "generate", "get", "me", "us", "a", "an", "the", "some", "tone", "tones",
			"sound", "sounds", "preset", "patch", "that", "which", "similar", "to", "of", "for", "song", "track", "close", "something"
		};

		private static string Clean(string? text)
		{
			if (string.IsNullOrWhiteSpace(text)) return string.Empty;
			StringBuilder builder = new();
			foreach (char character in text.Normalize(NormalizationForm.FormD))
			{
				if (character is >= ' ' and <= '~') builder.Append(character);
				else if (char.IsWhiteSpace(character)) builder.Append(' ');
				else if (character is '‘' or '’') builder.Append('\'');
				else if (character is '“' or '”') builder.Append('"');
				else if (character is '–' or '—') builder.Append('-');
			}
			return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
		}

		private static string? Shorten(string text, int limit)
		{
			if (text.Length == 0) return null;
			if (text.Length <= limit) return text;
			int space = text.LastIndexOf(' ', limit);
			return (space > limit / 2 ? text[..space] : text[..limit]).TrimEnd(' ', ',', '-', ':', ';', '.');
		}
	}
}
