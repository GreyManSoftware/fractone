namespace Fractal.Companion.Transport
{
	// Cuts SysEx frames (F0 .. F7) out of a raw byte stream. A serial port has no message boundaries: one read can hold
	// half a frame, several frames, or bytes that belong to no frame (line noise, a reply to a request that already timed
	// out mid-frame). Those are dropped rather than glued onto the next frame.
	public sealed class SysExFramer(int maxFrameBytes = 65_536)
	{
		private const byte Start = 0xF0;
		private const byte End = 0xF7;
		private readonly List<byte> _frame = [];
		private bool _inFrame;

		// Bytes thrown away so far: outside any frame, or a partial frame cut short.
		public long DiscardedBytes { get; private set; }

		public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> bytes)
		{
			List<byte[]> frames = [];
			foreach (byte value in bytes)
			{
				if (value == Start)
				{
					// A new start before the end: the earlier frame was cut short, so it can't be trusted.
					DropPartial();
					_frame.Add(value);
					_inFrame = true;
				}
				else if (!_inFrame)
				{
					DiscardedBytes++;
				}
				else if (value >= 0xF8)
				{
					// MIDI real-time bytes may sit inside SysEx without ending it.
				}
				else if (value == End)
				{
					_frame.Add(value);
					frames.Add([.. _frame]);
					_frame.Clear();
					_inFrame = false;
				}
				else if (value >= 0x80 || _frame.Count >= maxFrameBytes)
				{
					// Any other status byte ends SysEx in MIDI; a runaway frame is noise, not a reply.
					DropPartial();
					DiscardedBytes++;
				}
				else
				{
					_frame.Add(value);
				}
			}
			return frames;
		}

		private void DropPartial()
		{
			DiscardedBytes += _frame.Count;
			_frame.Clear();
			_inFrame = false;
		}
	}
}
