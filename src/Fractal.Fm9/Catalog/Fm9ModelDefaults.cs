using System.Globalization;
using System.Text.Json;
using Fractal.Application;
using Fractal.Companion.Protocol;

namespace Fractal.Companion.Catalog
{
	// The settings the FM9 loads when a block's model or type is selected. Selecting an Amp, Drive, Reverb, Delay... type
	// on the FM9 (front panel, editor or MIDI) resets that block's voicing to the type's defaults; writing the type number
	// into a record offline does not, so a generated "USA MK IIC+" kept the template amp's tone stack, bias and sag. The
	// defaults were captured from the FM9 itself (firmware 12, one type at a time over MIDI on a scratch edit buffer) and
	// only the words that differ between types are kept: words a type change leaves alone stay as they are, as on the FM9.
	public static class Fm9ModelDefaults
	{
		private static readonly Lazy<IReadOnlyDictionary<string, Family>> Families = new(Load);

		public static IReadOnlyCollection<string> BlockTypes => Families.Value.Keys.ToArray();

		public static bool HasDefaults(string blockType, int model) =>
			Families.Value.TryGetValue(blockType, out Family? family) && family.Models.ContainsKey(model);

		// The words (by parameter id) the FM9 loads for a type, or null when it was not captured.
		public static IReadOnlyDictionary<int, int>? For(string blockType, int model) =>
			Families.Value.TryGetValue(blockType, out Family? family) && family.Models.TryGetValue(model, out int[]? words)
				? family.Parameters.Select((id, index) => (id, word: words[index])).ToDictionary(pair => pair.id, pair => pair.word)
				: null;

		// The proposal setting that selects a block's type: the curated selector (Amp Model, Drive Model) or the dictionary
		// setting holding the type word.
		public static string? TypeSetting(string blockType)
		{
			if (!Families.Value.TryGetValue(blockType, out Family? family))
				return null;
			return Fm9QualifiedModelCatalog.SelectorParameter(blockType)
				?? Fm9BlockSettings.For(blockType).FirstOrDefault(setting => setting.Id == family.ModelParameter)?.Name;
		}

		// The type ordinal a setting value selects (a curated selector holds the ordinal; a dictionary type may be named).
		public static int? TypeOrdinal(string blockType, string value)
		{
			if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ordinal))
				return ordinal;
			if (TypeSetting(blockType) is string name && Fm9BlockSettings.Find(blockType, name) is { } setting)
			{
				try { return setting.Write(value); }
				catch (InvalidDataException) { return null; }
			}
			return null;
		}

		// The model's voicing as the designer shows it: dictionary settings (not the curated knobs, which stay as the player
		// set them) by name, as display text. Empty when the type was not captured.
		public static IReadOnlyDictionary<string, string> Settings(string blockType, int model)
		{
			if (For(blockType, model) is not { } words)
				return new Dictionary<string, string>();
			string? type = TypeSetting(blockType);
			Dictionary<string, string> settings = new(StringComparer.Ordinal);
			foreach (Fm9BlockSetting setting in Fm9BlockSettings.For(blockType))
				if (setting.Name != type && words.TryGetValue(setting.Id, out int word) && setting.Read(word) is string text)
					settings[setting.Name] = text;
			return settings;
		}

		// One channel row with the voicing of the type the settings select, when that differs from the type the row holds.
		// The type word itself is left to the caller's own writer; the settings are written over the voicing afterwards.
		public static byte[]? WithVoicing(ReadOnlySpan<byte> body, Fm9PresetBlockRecord record, int row, string blockType,
			IEnumerable<KeyValuePair<string, string>> settings)
		{
			ArgumentNullException.ThrowIfNull(record);
			ArgumentNullException.ThrowIfNull(settings);
			if (!Families.Value.TryGetValue(blockType, out Family? family) || TypeSetting(blockType) is not string name ||
				row < 0 || row >= record.ParameterRows || family.ModelParameter >= record.ParameterColumns)
				return null;
			string? value = settings.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
			if (value is null || TypeOrdinal(blockType, value) is not int model || !family.Models.TryGetValue(model, out int[]? words))
				return null;
			if (BitConverter.ToUInt16(body[Fm9ChannelParameterStorage.GetOffset(record, row, family.ModelParameter)..]) == model)
				return null;
			byte[] bytes = body.ToArray();
			for (int index = 0; index < family.Parameters.Count; index++)
			{
				int id = family.Parameters[index];
				if (id < record.ParameterColumns)
					BitConverter.TryWriteBytes(bytes.AsSpan(Fm9ChannelParameterStorage.GetOffset(record, row, id)), checked((ushort)words[index]));
			}
			return bytes;
		}

		// The template with each proposal block channel set to its type's FM9 voicing where the type changes.
		public static Fm9PresetDump Prepare(Fm9PresetDump template, ToneDesignProposal proposal)
		{
			ArgumentNullException.ThrowIfNull(template);
			ArgumentNullException.ThrowIfNull(proposal);
			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(template);
			byte[] bytes = body.Bytes.ToArray();
			bool changed = false;
			foreach (ToneDesignBlockProposal block in proposal.Blocks)
			{
				if (Fm9BlockSettings.EffectIdOf(block.BlockType, block.Instance) is not int effectId)
					continue;
				Fm9PresetBlockRecord? record = body.BlockRecords.FirstOrDefault(candidate => candidate.Offset >= 12 &&
					BitConverter.ToUInt16(body.Bytes.Span[(candidate.Offset - 12)..]) == effectId);
				if (record is null)
					continue;
				foreach (string channel in block.GetAuthoredChannels())
				{
					if (WithVoicing(bytes, record, channel[0] - 'A', block.BlockType, block.GetParameters(channel)) is byte[] voiced)
					{
						bytes = voiced;
						changed = true;
					}
				}
			}
			return changed ? Fm9PresetBodyEncoder.Reencode(template, bytes) : template;
		}

		private static IReadOnlyDictionary<string, Family> Load()
		{
			using Stream stream = typeof(Fm9ModelDefaults).Assembly.GetManifestResourceStream(
				"Fractal.Companion.Catalog.fm9-12-model-defaults.json")
				?? throw new InvalidOperationException("The bundled FM9 model defaults are unavailable.");
			Dictionary<string, FamilyDocument> document = JsonSerializer.Deserialize<Dictionary<string, FamilyDocument>>(stream)
				?? throw new InvalidDataException("The bundled FM9 model defaults are empty.");
			return document.ToDictionary(pair => pair.Key, pair => new Family(pair.Value.ModelParameter, pair.Value.Parameters,
				pair.Value.Models.ToDictionary(model => int.Parse(model.Key, CultureInfo.InvariantCulture), model => model.Value)),
				StringComparer.OrdinalIgnoreCase);
		}

		private sealed record Family(int ModelParameter, IReadOnlyList<int> Parameters, IReadOnlyDictionary<int, int[]> Models);

		private sealed record FamilyDocument(int ModelParameter, int[] Parameters, Dictionary<string, int[]> Models);
	}
}
