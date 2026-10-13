namespace Fractal.Companion.Protocol
{
	public sealed record Fm9BulkReadResult(
		int EffectId,
		IReadOnlyList<int> Values);

	public static class Fm9BulkRead
	{
		public static bool TryParse(
			IReadOnlyList<FractalSysExFrame> frames,
			out Fm9BulkReadResult? result)
		{
			result = null;
			FractalSysExFrame? header = frames.FirstOrDefault(frame =>
				FractalSysExCodec.TryGetBroadcastHeaderEffectId(
					frame.Bytes.Span,
					out _));

			if (header is null ||
				!FractalSysExCodec.TryGetBroadcastHeaderEffectId(
					header.Value.Bytes.Span,
					out int effectId))
			{
				return false;
			}

			List<int> values = [];

			foreach (FractalSysExFrame frame in frames)
			{
				ReadOnlySpan<byte> bytes = frame.Bytes.Span;

				if (!FractalSysExCodec.IsValidFm9Frame(bytes) || bytes[5] != 0x75)
				{
					continue;
				}

				ReadOnlySpan<byte> body = bytes.Slice(8, bytes.Length - 10);

				for (int offset = 0; offset + 2 < body.Length; offset += 3)
				{
					values.Add(
						body[offset] |
						(body[offset + 1] << 7) |
						((body[offset + 2] & 0x03) << 14));
				}
			}

			bool hasEndFrame = frames.Any(frame =>
				FractalSysExCodec.IsValidFm9Frame(frame.Bytes.Span) &&
				frame.Bytes.Span[5] == 0x76);

			if (!hasEndFrame || values.Count == 0)
			{
				return false;
			}

			result = new Fm9BulkReadResult(effectId, values);
			return true;
		}

		public static float ToNormalized(int wireValue)
		{
			if (wireValue is < 0 or > 65_534)
			{
				throw new ArgumentOutOfRangeException(
					nameof(wireValue),
					"FM9 bulk-read values must be between 0 and 65534.");
			}

			return wireValue / 65_534f;
		}
	}
}
