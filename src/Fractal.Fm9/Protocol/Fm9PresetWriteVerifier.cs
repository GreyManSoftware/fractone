namespace Fractal.Companion.Protocol
{
	public sealed record Fm9NormalizedFieldExpectation(
		int ParameterId,
		string CatalogName,
		int BodyOffset,
		ushort AuthoredRaw,
		ushort NormalizedRaw);

	public sealed record Fm9PresetNormalizationExpectation(
		Fm9PresetDump CanonicalReadback,
		IReadOnlyList<Fm9NormalizedFieldExpectation> Fields);

	public static class Fm9PresetWriteVerifier
	{
		private static readonly IReadOnlyDictionary<int, string> AllowedAmpNormalizations =
			new Dictionary<int, string>
			{
				[27] = "DISTORT_OFFSET1",
				[38] = "DISTORT_WSHPF",
				[43] = "DISTORT_FBTYPE",
				[44] = "DISTORT_PI_RATIO",
				[70] = "DISTORT_EXCURSIONTIME",
				[71] = "DISTORT_RECOVERYTIME",
				[113] = "DISTORT_TRIODE2EXTIME",
				[114] = "DISTORT_TRIODE2RECTIME",
				// Hardness words derived from the tube and model: a from-scratch Ride carrying the dictionary defaults
				// (0 / 58252 / 0) came back with 43711 / 64078 / 53738 on channel A (505, 2026-10-13).
				[87] = "DISTORT_CFGRID",
				[109] = "DISTORT_GRIDHARDNESS",
				[119] = "DISTORT_CFHARDNESS"
			};

		// Checks what the FM9 holds after importing a preset against what was sent. The FM9 recalculates the proven
		// model-dependent Amp values above when it loads an amp model (seen on the Ride template's Amp); the same columns are
		// allowed on every Amp record and channel of any preset. Anything else that differs means the import changed the
		// preset. Returns the recalculated fields (empty when the body came back exactly).
		public static IReadOnlyList<Fm9NormalizedFieldExpectation> VerifyImportedBody(Fm9PresetDump authored, Fm9PresetDump imported)
		{
			ArgumentNullException.ThrowIfNull(authored);
			ArgumentNullException.ThrowIfNull(imported);
			if (authored.FormatVersion != imported.FormatVersion ||
				!string.Equals(authored.PresetName, imported.PresetName, StringComparison.Ordinal))
				throw new InvalidDataException("The FM9's edit buffer does not hold the preset that was sent.");
			Fm9PresetBody authoredBody = Fm9PresetBodyDecoder.Decode(authored);
			Fm9PresetBody importedBody = Fm9PresetBodyDecoder.Decode(imported);
			if (authoredBody.Bytes.Span.SequenceEqual(importedBody.Bytes.Span))
				return [];
			if (authoredBody.Bytes.Length != importedBody.Bytes.Length ||
				!authoredBody.GridCells.SequenceEqual(importedBody.GridCells) ||
				!authoredBody.BlockRecords.SequenceEqual(importedBody.BlockRecords))
				throw new InvalidDataException("The FM9 changed the grid or block layout of the preset on import.");

			Dictionary<int, int> allowed = [];
			foreach (Fm9UploadedBlock amp in Fm9UploadedPreset.Read(authored).Blocks.Where(block => block.BlockType == "Amp" && block.Record is not null))
				for (int row = 0; row < amp.Record!.ParameterRows; row++)
					foreach (int id in AllowedAmpNormalizations.Keys.Where(id => id < amp.Record.ParameterColumns))
						allowed[Fm9ChannelParameterStorage.GetOffset(amp.Record, row, id)] = id;
			ReadOnlySpan<byte> before = authoredBody.Bytes.Span, after = importedBody.Bytes.Span;
			List<int> unexpected = [];
			for (int offset = 0; offset < before.Length; offset++)
				if (before[offset] != after[offset] && !allowed.ContainsKey(offset) && !allowed.ContainsKey(offset - 1))
					unexpected.Add(offset);
			if (unexpected.Count > 0)
				throw new InvalidDataException(
					$"The FM9's copy differs from the preset sent in {unexpected.Count} byte(s) outside the Amp values it recalculates " +
					$"(body offsets {string.Join(", ", unexpected.Take(8))}{(unexpected.Count > 8 ? ", ..." : "")}).");
			List<Fm9NormalizedFieldExpectation> fields = [];
			foreach ((int offset, int id) in allowed.OrderBy(pair => pair.Key))
				if (ReadUInt16(before, offset) != ReadUInt16(after, offset))
					fields.Add(new(id, AllowedAmpNormalizations[id], offset, ReadUInt16(before, offset), ReadUInt16(after, offset)));
			return fields;
		}

		public static Fm9PresetNormalizationExpectation CreateNormalizationExpectation(
			Fm9PresetDump authored,
			Fm9PresetDump canonicalHardwareReadback)
		{
			ArgumentNullException.ThrowIfNull(authored);
			ArgumentNullException.ThrowIfNull(canonicalHardwareReadback);

			if (!string.Equals(
				authored.PresetName,
				canonicalHardwareReadback.PresetName,
				StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					"The normalization reference has a different preset identity.");
			}

			Fm9PresetBody authoredBody = Fm9PresetBodyDecoder.Decode(authored);
			Fm9PresetBody canonicalBody = Fm9PresetBodyDecoder.Decode(canonicalHardwareReadback);

			if (!authoredBody.GridCells.SequenceEqual(canonicalBody.GridCells))
			{
				throw new InvalidDataException(
					"The normalization reference changes the grid or route state.");
			}

			if (!authoredBody.BlockRecords.SequenceEqual(canonicalBody.BlockRecords))
			{
				throw new InvalidDataException(
					"The normalization reference changes the block-record layout.");
			}

			Fm9BlockRecordTemplateProfile profile =
				Fm9BlockRecordTemplateProfile.CreateRideBaseline(authoredBody);
			Fm9PresetBlockRecord ampRecord = profile.GetRecordForExistingBlock(
				authoredBody,
				"Amp",
				1);
			Dictionary<int, int> allowedOffsets = AllowedAmpNormalizations.Keys.ToDictionary(
				parameterId => GetParameterOffset(ampRecord, parameterId),
				parameterId => parameterId);
			List<int> differences = [];

			for (int offset = 0; offset < authoredBody.Bytes.Length; offset++)
			{
				if (authoredBody.Bytes.Span[offset] != canonicalBody.Bytes.Span[offset])
				{
					differences.Add(offset);
				}
			}

			if (differences.Count == 0)
			{
				throw new InvalidDataException(
					"The normalization reference does not contain a device normalization.");
			}

			HashSet<int> allowedBytes = allowedOffsets.Keys
				.SelectMany(offset => new[] { offset, offset + 1 })
				.ToHashSet();
			int[] unsupportedDifferences = differences
				.Where(offset => !allowedBytes.Contains(offset))
				.ToArray();

			if (unsupportedDifferences.Length > 0)
			{
				throw new InvalidDataException(
					"The normalization reference differs outside the proven Amp normalization fields at body offsets " +
					string.Join(", ", unsupportedDifferences) + ".");
			}

			List<Fm9NormalizedFieldExpectation> fields = [];

			foreach ((int offset, int parameterId) in allowedOffsets.OrderBy(pair => pair.Key))
			{
				ushort authoredRaw = ReadUInt16(authoredBody.Bytes.Span, offset);
				ushort normalizedRaw = ReadUInt16(canonicalBody.Bytes.Span, offset);

				if (authoredRaw != normalizedRaw)
				{
					fields.Add(new Fm9NormalizedFieldExpectation(
						parameterId,
						AllowedAmpNormalizations[parameterId],
						offset,
						authoredRaw,
						normalizedRaw));
				}
			}

			// Every difference is already confined to the proven fields above; which of them the FM9 recalculates depends on
			// how far the authored values were from the new model's, so any non-empty subset is legitimate.
			if (fields.Count == 0)
			{
				throw new InvalidDataException(
					"The normalization reference does not change any proven model-dependent Amp field.");
			}

			return new Fm9PresetNormalizationExpectation(canonicalHardwareReadback, fields);
		}

		public static void VerifyCanonicalReadback(
			Fm9PresetNormalizationExpectation expectation,
			Fm9PresetDump actualReadback)
		{
			ArgumentNullException.ThrowIfNull(expectation);
			ArgumentNullException.ThrowIfNull(actualReadback);

			if (expectation.CanonicalReadback.HasSameRawPatch(actualReadback))
			{
				return;
			}

			if (expectation.CanonicalReadback.FormatVersion != actualReadback.FormatVersion ||
				!string.Equals(
					expectation.CanonicalReadback.PresetName,
					actualReadback.PresetName,
					StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					"The stored-preset readback identity does not match the canonical hardware-normalized preset.");
			}

			Fm9PresetBody expectedBody =
				Fm9PresetBodyDecoder.Decode(expectation.CanonicalReadback);
			Fm9PresetBody actualBody = Fm9PresetBodyDecoder.Decode(actualReadback);

			if (!expectedBody.Bytes.Span.SequenceEqual(actualBody.Bytes.Span))
			{
				throw new InvalidDataException(
					"The stored-preset readback contains stable body-byte differences from the canonical hardware-normalized preset.");
			}
		}

		public static void VerifyExactBodyReadback(
			Fm9PresetDump expected,
			Fm9PresetDump actual)
		{
			ArgumentNullException.ThrowIfNull(expected);
			ArgumentNullException.ThrowIfNull(actual);

			if (expected.FormatVersion != actual.FormatVersion ||
				!string.Equals(expected.PresetName, actual.PresetName, StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					"The stored-preset readback identity does not match the authored preset.");
			}

			Fm9PresetBody expectedBody = Fm9PresetBodyDecoder.Decode(expected);
			Fm9PresetBody actualBody = Fm9PresetBodyDecoder.Decode(actual);

			if (!expectedBody.Bytes.Span.SequenceEqual(actualBody.Bytes.Span))
			{
				throw new InvalidDataException(
					"The stored-preset readback contains body-byte differences from the authored preset.");
			}
		}

		private static int GetParameterOffset(Fm9PresetBlockRecord record, int parameterId)
		{
			const int headerWordCount = 23;
			const int firstStoredParameterId = 6;
			return record.Offset + ((headerWordCount +
				(parameterId - firstStoredParameterId)) * sizeof(ushort));
		}

		private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset)
		{
			return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
		}
	}
}
