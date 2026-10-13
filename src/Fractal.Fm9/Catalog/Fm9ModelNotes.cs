using System.Text;
using System.Text.Json;

namespace Fractal.Companion.Catalog
{
	// How FM9 models and controls behave beyond what the setting guide says, measured by re-amping one DI through each
	// change (fm9-12-model-notes.json). The AI reads them when it chooses models and again when it sets their controls,
	// e.g. that the USA MK IIC++ runs colder than the IIC+ at the same knob numbers. Generic only: never per-song settings.
	public static class Fm9ModelNotes
	{
		private static readonly Lazy<IReadOnlyDictionary<string, BlockNotes>> Blocks = new(Load);

		// The block's general notes, then the model's own.
		public static IReadOnlyList<string> For(string blockType, string? model)
		{
			if (!Blocks.Value.TryGetValue(blockType, out BlockNotes? notes))
				return [];
			IReadOnlyList<string> own = model is not null && notes.Models?.TryGetValue(model, out string[]? found) == true ? found! : [];
			return [.. notes.General ?? [], .. own];
		}

		public static string DescribeForPrompt()
		{
			StringBuilder text = new("FM9 model notes, measured on the hardware (generic behaviour, not settings for any song):");
			foreach ((string blockType, BlockNotes notes) in Blocks.Value)
			{
				foreach (string note in notes.General ?? [])
					text.Append(Environment.NewLine).Append("- ").Append(blockType).Append(": ").Append(note);
				foreach ((string model, string[] modelNotes) in notes.Models ?? [])
					foreach (string note in modelNotes)
						text.Append(Environment.NewLine).Append("- ").Append(blockType).Append(' ').Append(model).Append(": ").Append(note);
			}
			return text.ToString();
		}

		private static IReadOnlyDictionary<string, BlockNotes> Load()
		{
			using Stream stream = typeof(Fm9ModelNotes).Assembly.GetManifestResourceStream("Fractal.Companion.Catalog.fm9-12-model-notes.json")
				?? throw new InvalidOperationException("The bundled model notes are unavailable.");
			NotesDocument document = JsonSerializer.Deserialize<NotesDocument>(stream) ?? new(string.Empty, []);
			return new Dictionary<string, BlockNotes>(document.Blocks, StringComparer.OrdinalIgnoreCase);
		}

		private sealed record NotesDocument(string Source, Dictionary<string, BlockNotes> Blocks);

		private sealed record BlockNotes(string[]? General, Dictionary<string, string[]>? Models);
	}
}
