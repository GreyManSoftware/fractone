namespace Fractal.Companion.Protocol
{
	public sealed record Fm9BlockRecordTemplate(
		string BlockType,
		int Instance,
		ushort EffectId,
		ushort ParameterColumns,
		ushort ParameterRows,
		ReadOnlyMemory<byte> SceneStatePrefix,
		ReadOnlyMemory<byte> SerializedRecord);

	public sealed record Fm9TemplateParameterDefinition(
		string Name,
		int ParameterId,
		double Minimum,
		double Maximum,
		bool UsesLogarithmicScaling = false,
		bool IsDiscrete = false);

	public sealed record Fm9TemplateBlockDefinition(
		string BlockType,
		int Instance,
		ushort EffectId,
		ushort ParameterColumns,
		ushort ParameterRows,
		IReadOnlyList<Fm9TemplateParameterDefinition> Parameters,
		int? RecordOffset = null,
		Fm9TemplateBindingEvidence? BindingEvidence = null);

	public sealed class Fm9BlockRecordTemplateProfile
	{
		private static readonly IReadOnlyList<Fm9TemplateBlockDefinition> RideBaselineBlocks =
		[
			new("Input", 1, 37, 10, 4, []),
			new("Gate/Expander", 1, 146, 19, 4,
			[
				new("Threshold", 0, -100, 0),
				new("Attack", 1, 0.1, 1_000, true),
				new("Hold", 2, 1, 1_000, true),
				new("Release", 3, 1, 1_000, true),
				new("Ratio", 4, 1, 20, true),
				new("Low Cut", 6, 10, 1_000, true),
				new("High Cut", 7, 200, 20_000, true),
				new("Level", 9, -80, 20)
			]),
			new("Drive", 1, 118, 44, 4,
			[
				new("Drive Model", 0, 0, 86, IsDiscrete: true),
				new("Drive", 1, 0, 10),
				new("Tone", 2, 0, 10),
				new("Level", 3, 0, 10),
				new("Mix", 4, 0, 100)
			]),
			new("Amp", 1, 58, 147, 4,
			[
				new("Level", 1, -80, 20),
				new("Gain", 11, 0, 10),
				new("Bass", 12, 0, 10),
				new("Mid", 13, 0, 10),
				new("Treble", 14, 0, 10),
				new("Master", 15, 0, 10),
				// Log like every other cut: FM9-Edit's typecodes 0x241/0x240 match the hardware-proven Cab cuts.
				new("Low Cut", 16, 10, 1_000, true),
				new("High Cut", 17, 400, 40_000, true),
				new("Presence", 30, 0, 10)
			]),
			new("Cab", 1, 62, 106, 4, []),
			new("Parametric EQ", 1, 54, 33, 4,
			[
				new("Frequency 1", 0, 20, 2_000, true),
				new("Frequency 2", 1, 100, 10_000, true),
				new("Frequency 3", 2, 100, 10_000, true),
				new("Frequency 4", 3, 100, 10_000, true),
				new("Frequency 5", 4, 200, 20_000, true),
				new("Q 1", 5, 0.1, 10, true),
				new("Q 2", 6, 0.1, 10, true),
				new("Q 3", 7, 0.1, 10, true),
				new("Q 4", 8, 0.1, 10, true),
				new("Q 5", 9, 0.1, 10, true),
				new("Gain 1", 10, -20, 20),
				new("Gain 2", 11, -20, 20),
				new("Gain 3", 12, -20, 20),
				new("Gain 4", 13, -20, 20),
				new("Gain 5", 14, -20, 20)
			]),
			new("Reverb", 1, 66, 73, 4,
			[
				new("Mix", 0, 0, 100),
				new("Level", 1, -80, 20),
				new("Balance", 2, -100, 100),
				new("Time", 11, 0.1, 100),
				new("High Cut", 12, 200, 20_000, true),
				new("Low Cut", 20, 20, 2_000, true),
				new("Pre-Delay", 19, 0, 1_000)
			]),
			new("Output", 1, 42, 26, 4, [])
		];

		private readonly IReadOnlyDictionary<(string BlockType, int Instance), Fm9BlockRecordTemplate> _templates;
		private readonly IReadOnlyDictionary<(string BlockType, int Instance), Fm9TemplateBlockDefinition> _definitions;

		private Fm9BlockRecordTemplateProfile(
			IReadOnlyDictionary<(string BlockType, int Instance), Fm9BlockRecordTemplate> templates,
			IReadOnlyDictionary<(string BlockType, int Instance), Fm9TemplateBlockDefinition> definitions)
		{
			_templates = templates;
			_definitions = definitions;
		}

		public IReadOnlyList<Fm9BlockRecordTemplate> Templates => _templates.Values.ToArray();
		public IReadOnlyList<Fm9TemplateBlockDefinition> Definitions => _definitions.Values.ToArray();

		// The verified parameter layout of a block family (from its instance-1 Ride definition), for any instance.
		public static Fm9TemplateBlockDefinition? FindFamilyDefinition(string blockType) =>
			RideBaselineBlocks.FirstOrDefault(block => string.Equals(block.BlockType, blockType, StringComparison.OrdinalIgnoreCase));

		public static Fm9BlockRecordTemplateProfile CreateRideBaseline(Fm9PresetBody baseline)
		{
			ArgumentNullException.ThrowIfNull(baseline);
			// Qualified alternate I/O instances share the logical endpoint record layouts.
			return Create(baseline, RideBaselineBlocks.Select(block =>
				block.BlockType is "Input" or "Output"
					? block with { EffectId = Fm9PresetBodyGridEditor.ResolveInputOutputEffect(baseline, block.BlockType) }
					: block).ToArray());
		}

		public static Fm9BlockRecordTemplateProfile Create(
			Fm9PresetBody baseline,
			IReadOnlyList<Fm9TemplateBlockDefinition> definitions)
		{
			ArgumentNullException.ThrowIfNull(baseline);
			ArgumentNullException.ThrowIfNull(definitions);
			Dictionary<(string BlockType, int Instance), Fm9BlockRecordTemplate> templates = [];
			Dictionary<(string BlockType, int Instance), Fm9TemplateBlockDefinition> definitionMap = [];

			foreach (Fm9TemplateBlockDefinition supportedBlock in definitions)
			{
				if (!definitionMap.TryAdd((supportedBlock.BlockType, supportedBlock.Instance), supportedBlock))
				{
					throw new InvalidDataException($"The FM9 template profile duplicates '{supportedBlock.BlockType} {supportedBlock.Instance}'.");
				}

				ValidateDefinition(supportedBlock);
				Fm9PresetBlockRecord record = ResolveRecord(baseline, supportedBlock);
				byte[] sceneStatePrefix = baseline.Bytes.Span
					.Slice(record.Offset - sizeof(ushort), sizeof(ushort))
					.ToArray();
				byte[] state = baseline.Bytes.Span.Slice(record.Offset, record.Length).ToArray();
				templates.Add(
					(supportedBlock.BlockType, supportedBlock.Instance),
					new Fm9BlockRecordTemplate(
						supportedBlock.BlockType,
						supportedBlock.Instance,
						supportedBlock.EffectId,
						supportedBlock.ParameterColumns,
						supportedBlock.ParameterRows,
						sceneStatePrefix,
						state));
			}

			return new Fm9BlockRecordTemplateProfile(templates, definitionMap);
		}

		public Fm9BlockRecordTemplate GetTemplate(string blockType, int instance)
		{
			Fm9TemplateBlockDefinition supportedBlock = FindSupportedBlock(blockType, instance);
			return _templates[(supportedBlock.BlockType, supportedBlock.Instance)];
		}

		public Fm9PresetBlockRecord GetRecordForExistingBlock(
			Fm9PresetBody target,
			string blockType,
			int instance)
		{
			ArgumentNullException.ThrowIfNull(target);
			return ResolveRecord(target, FindSupportedBlock(blockType, instance));
		}

		public Fm9TemplateParameterDefinition GetParameter(
			string blockType,
			int instance,
			string parameterName)
		{
			Fm9TemplateBlockDefinition block = FindSupportedBlock(blockType, instance);

			return block.Parameters.SingleOrDefault(parameter =>
				string.Equals(parameter.Name, parameterName, StringComparison.OrdinalIgnoreCase))
				?? throw new NotSupportedException(
					$"FM9 parameter '{parameterName}' is not verified for offline export on '{blockType} {instance}'.");
		}

		public byte[] ReuseTemplateForExistingBlock(
			Fm9PresetBody target,
			string blockType,
			int instance)
		{
			ArgumentNullException.ThrowIfNull(target);
			Fm9BlockRecordTemplate template = GetTemplate(blockType, instance);
			Fm9PresetBlockRecord targetRecord = GetRecordForExistingBlock(target, blockType, instance);

			if (template.SceneStatePrefix.Length != sizeof(ushort) ||
				template.SerializedRecord.Length != targetRecord.Length)
			{
				throw new InvalidDataException("The FM9 block-record template does not match the existing block record.");
			}

			byte[] bytes = target.Bytes.ToArray();
			template.SceneStatePrefix.Span.CopyTo(
				bytes.AsSpan(targetRecord.Offset - sizeof(ushort), sizeof(ushort)));
			template.SerializedRecord.Span.CopyTo(bytes.AsSpan(targetRecord.Offset, targetRecord.Length));
			return bytes;
		}

		private Fm9TemplateBlockDefinition FindSupportedBlock(string blockType, int instance)
		{
			if (string.IsNullOrWhiteSpace(blockType))
			{
				throw new ArgumentException("An FM9 block type is required.", nameof(blockType));
			}

			return _definitions.Values.SingleOrDefault(candidate =>
				candidate.Instance == instance &&
				string.Equals(candidate.BlockType, blockType, StringComparison.OrdinalIgnoreCase))
				?? throw new NotSupportedException(
					$"FM9 block-record templates do not support '{blockType} {instance}'.");
		}

		private static Fm9PresetBlockRecord ResolveRecord(
			Fm9PresetBody body,
			Fm9TemplateBlockDefinition supportedBlock)
		{
			int gridMatches = body.GridCells.Count(cell => cell.EffectId == supportedBlock.EffectId);

			if (gridMatches != 1)
			{
				throw new InvalidOperationException(
					$"The preset does not contain exactly one existing {supportedBlock.BlockType} {supportedBlock.Instance} block.");
			}

			List<Fm9PresetBlockRecord> records = body.BlockRecords
				.Where(record =>
					// A record bound by its offset (an uploaded preset) may be an older, narrower one: firmware only appended
					// columns, so it serves as long as it holds every profiled setting.
					(supportedBlock.RecordOffset is null
						? record.ParameterColumns == supportedBlock.ParameterColumns
						: record.ParameterColumns <= supportedBlock.ParameterColumns &&
							supportedBlock.Parameters.All(parameter => parameter.ParameterId < record.ParameterColumns)) &&
					record.ParameterRows == supportedBlock.ParameterRows &&
					record.Offset >= sizeof(ushort) &&
					record.Length == (23 + (record.ParameterColumns * record.ParameterRows)) * sizeof(ushort) &&
					record.Offset + record.Length <= body.Bytes.Length)
				.ToList();

			if (supportedBlock.RecordOffset is int recordOffset)
			{
				records = records.Where(record => record.Offset == recordOffset).ToList();
			}

			return records.Count == 1
				? records[0]
				: throw new InvalidOperationException(
					$"The preset does not contain exactly one serialized record for existing {supportedBlock.BlockType} {supportedBlock.Instance}.");
		}

		private static void ValidateDefinition(Fm9TemplateBlockDefinition definition)
		{
			if (string.IsNullOrWhiteSpace(definition.BlockType) || definition.Instance < 1 ||
				definition.EffectId == 0 || definition.ParameterColumns == 0 || definition.ParameterRows == 0 ||
				definition.RecordOffset is < 0)
			{
				throw new InvalidDataException("An FM9 template block definition is invalid.");
			}

			HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
			HashSet<int> ids = [];

			foreach (Fm9TemplateParameterDefinition parameter in definition.Parameters)
			{
				if (string.IsNullOrWhiteSpace(parameter.Name) || parameter.ParameterId < 0 ||
					parameter.ParameterId >= definition.ParameterColumns ||
					!double.IsFinite(parameter.Minimum) || !double.IsFinite(parameter.Maximum) ||
					parameter.Minimum >= parameter.Maximum ||
					(parameter.UsesLogarithmicScaling && parameter.Minimum <= 0) ||
					!names.Add(parameter.Name) || !ids.Add(parameter.ParameterId))
				{
					throw new InvalidDataException(
						$"The FM9 template parameter definition '{definition.BlockType} {definition.Instance} {parameter.Name}' is invalid.");
				}
			}
		}
	}
}
