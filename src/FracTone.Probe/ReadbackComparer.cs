using Fractal.Companion.Protocol;

namespace FracTone.Probe
{
	public sealed record RecomputedWord(int EffectId, int Channel, int ParameterId, ushort Sent, ushort ReadBack);

	public sealed record ReadbackComparison(
		bool Matches,
		bool Identical,
		IReadOnlyList<RecomputedWord> Recomputed,
		IReadOnlyList<int> UnexpectedOffsets,
		string Summary);

	// Fm9PresetWriteVerifier's readback rule for any gen-3 device, without FM9-Edit's dictionary: the decoded body must
	// equal what was sent except body word 0 (the scene playing) and the Amp words the script says the device recomputes on
	// load, on every channel of every Amp record. Name, format version, grid and record layout must match exactly.
	public static class ReadbackComparer
	{
		private const int EffectIdBytesBeforeRecord = 12;

		public static ReadbackComparison Compare(Gen3Device device, ProbeScript script, Fm9PresetDump sent, Fm9PresetDump readBack)
		{
			if (sent.FormatVersion != readBack.FormatVersion)
				return Fail($"Format version changed: 0x{sent.FormatVersion:X4} sent, 0x{readBack.FormatVersion:X4} read back.");
			if (!string.Equals(sent.PresetName, readBack.PresetName, StringComparison.Ordinal))
				return Fail("The buffer holds a different preset (its name differs): the load did not take.");
			Fm9PresetBody before = Fm9PresetBodyDecoder.Decode(sent, device), after = Fm9PresetBodyDecoder.Decode(readBack, device);
			if (before.Bytes.Length != after.Bytes.Length || !before.GridCells.SequenceEqual(after.GridCells) ||
				!before.BlockRecords.SequenceEqual(after.BlockRecords))
				return Fail("The device changed the grid or the block-record layout on load.");

			Dictionary<int, (int Effect, int Channel, int Id)> allowed = [];
			ReadOnlySpan<byte> sentBody = before.Bytes.Span, readBody = after.Bytes.Span;
			foreach (Fm9PresetBlockRecord record in before.BlockRecords.Where(record => record.Offset >= EffectIdBytesBeforeRecord))
			{
				int effect = BitConverter.ToUInt16(sentBody[(record.Offset - EffectIdBytesBeforeRecord)..]);
				if (!script.AmpEffectIds.Contains(effect)) continue;
				for (int channel = 0; channel < record.ParameterRows; channel++)
					foreach (int id in script.RecomputedAmpParameters.Where(id => id < record.ParameterColumns))
						allowed[Fm9ChannelParameterStorage.GetOffset(record, channel, id)] = (effect, channel, id);
			}

			List<int> unexpected = [];
			// Word 0 is the scene playing, which a scene change sets without editing anything.
			for (int offset = 2; offset < sentBody.Length; offset++)
				if (sentBody[offset] != readBody[offset] && !allowed.ContainsKey(offset) && !allowed.ContainsKey(offset - 1))
					unexpected.Add(offset);
			List<RecomputedWord> recomputed = [];
			foreach ((int offset, (int effect, int channel, int id)) in allowed.OrderBy(pair => pair.Key))
			{
				ushort was = BitConverter.ToUInt16(sentBody[offset..]), now = BitConverter.ToUInt16(readBody[offset..]);
				if (was != now) recomputed.Add(new RecomputedWord(effect, channel, id, was, now));
			}
			bool identical = sentBody[2..].SequenceEqual(readBody[2..]);
			string summary = unexpected.Count > 0
				? $"{unexpected.Count} byte(s) differ outside the recomputed Amp words (body offsets {string.Join(", ", unexpected.Take(8))}{(unexpected.Count > 8 ? ", ..." : "")})."
				: identical ? "Read back exactly as sent." : $"Read back as sent apart from {recomputed.Count} recomputed Amp word(s).";
			return new ReadbackComparison(unexpected.Count == 0, identical, recomputed, unexpected, summary);
		}

		// The same preset, as far as the user is concerned: same name and body apart from word 0 (the scene playing).
		public static bool SameContent(Gen3Device device, Fm9PresetDump left, Fm9PresetDump right) =>
			left.PresetName == right.PresetName && left.FormatVersion == right.FormatVersion &&
			Fm9PresetBodyDecoder.Decode(left, device).Bytes.Span[2..].SequenceEqual(Fm9PresetBodyDecoder.Decode(right, device).Bytes.Span[2..]);

		private static ReadbackComparison Fail(string summary) => new(false, false, [], [], summary);
	}
}
