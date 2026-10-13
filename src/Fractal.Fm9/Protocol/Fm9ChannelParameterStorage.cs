namespace Fractal.Companion.Protocol
{
	// The one place that decides where a block record keeps each channel's value of a parameter.
	//
	// A record is 17 header words followed by four channel rows of ParameterColumns words, then 6 trailing words. Every
	// parameter is stored per channel at word 17 + channel * columns + id. Ids 6+ were proven on hardware for Amp A-D under
	// the older reading "23 header words, rows of ids 6+", which addresses the same words. Ids 0-5 (Drive Model/Drive/Tone/
	// Level/Mix, Amp Level, Cab Bank/Type, PEQ Frequency 1-5 and Q 1, Gate Threshold-Ratio, Reverb Mix/Level/Balance) sit
	// at the start of each row: an FM9-saved preset whose PEQ channel A was edited (90/240/750/2200/6600 Hz) keeps the
	// FM9 default bands (200/400/800/1600/3200 Hz, Q 0.707) in those words of channels B-D, and on hardware a scene on
	// PEQ channel C changed a re-amped take by what channel C's own frequencies and Q predicted.
	public static class Fm9ChannelParameterStorage
	{
		public const int ChannelCount = 4;
		// Ids 0-5 open each channel row. They were once read as one block-wide header value, so proposals saved then
		// leave them to channel A (see Fm9ProposalPresetExporter.ChannelParameters).
		public const int RowStartParameterCount = 6;
		private const int FirstRowWord = 17;

		public static int GetOffset(Fm9PresetBlockRecord record, int channel, int parameterId)
		{
			ArgumentNullException.ThrowIfNull(record);
			if (channel < 0 || channel >= record.ParameterRows ||
				parameterId < 0 || parameterId >= record.ParameterColumns)
			{
				throw new ArgumentOutOfRangeException(nameof(parameterId));
			}

			return record.Offset + ((FirstRowWord + (channel * record.ParameterColumns) + parameterId) * sizeof(ushort));
		}

		// Copies every parameter of one channel onto another, so a channel can start as a copy and differ only where edited.
		public static byte[] WithChannelCopy(Fm9PresetBody source, Fm9PresetBlockRecord record, int fromChannel, int toChannel)
		{
			ArgumentNullException.ThrowIfNull(source);
			ArgumentNullException.ThrowIfNull(record);
			if (record.ParameterRows != ChannelCount)
				throw new NotSupportedException("Only four-channel records can be copied.");
			int rowBytes = record.ParameterColumns * sizeof(ushort);
			int from = GetOffset(record, fromChannel, 0);
			int to = GetOffset(record, toChannel, 0);
			if (to + rowBytes > record.Offset + record.Length)
				throw new InvalidDataException("The channel row does not fit inside its block record.");
			byte[] bytes = source.Bytes.ToArray();
			source.Bytes.Span.Slice(from, rowBytes).CopyTo(bytes.AsSpan(to, rowBytes));
			return bytes;
		}
	}
}
