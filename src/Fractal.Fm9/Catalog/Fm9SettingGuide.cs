using System.Text.Json;

namespace Fractal.Companion.Catalog
{
	// What each FM9 setting does to the sound, one sentence per setting, from Fractal's wiki and Blocks Guide (built by
	// tools/build-setting-guide.py). The AI reads it to decide how to set a knob; the designer shows it as help. Settings
	// Fractal does not document are marked "Undocumented: ..." rather than guessed.
	public static class Fm9SettingGuide
	{
		private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Blocks = new(Load);

		public static string? Describe(string blockType, string setting) =>
			Blocks.Value.TryGetValue(blockType, out IReadOnlyDictionary<string, string>? settings) &&
			settings.TryGetValue(setting, out string? text) ? text : null;

		public static IReadOnlyDictionary<string, string> For(string blockType) =>
			Blocks.Value.TryGetValue(blockType, out IReadOnlyDictionary<string, string>? settings) ? settings : new Dictionary<string, string>();

		private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load()
		{
			using Stream? stream = typeof(Fm9SettingGuide).Assembly.GetManifestResourceStream("Fractal.Companion.Catalog.fm9-12-setting-guide.json");
			if (stream is null)
				return new Dictionary<string, IReadOnlyDictionary<string, string>>();
			GuideDocument document = JsonSerializer.Deserialize<GuideDocument>(stream) ?? new([], []);
			return document.Blocks.ToDictionary(pair => pair.Key,
				pair => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(pair.Value, StringComparer.OrdinalIgnoreCase),
				StringComparer.OrdinalIgnoreCase);
		}

		private sealed record GuideDocument(string[] Sources, Dictionary<string, Dictionary<string, string>> Blocks);
	}
}
