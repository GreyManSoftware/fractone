namespace Fractal.Core
{
	public sealed record MultichannelAudioBuffer(
		int SampleRate,
		IReadOnlyList<IReadOnlyList<float>> Channels)
	{
		public void Validate()
		{
			if (SampleRate <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(SampleRate));
			}
			ArgumentNullException.ThrowIfNull(Channels);
			if (Channels.Count is < 1 or > 2)
			{
				throw new ArgumentException("Use mono or stereo audio.", nameof(Channels));
			}
			int count = Channels[0]?.Count ?? 0;
			if (count == 0 || Channels.Any(channel => channel is null || channel.Count != count))
			{
				throw new ArgumentException("Audio channels must contain equal, non-empty sample frames.", nameof(Channels));
			}
			foreach (IReadOnlyList<float> channel in Channels)
			{
				if (channel.Any(sample => !float.IsFinite(sample)))
				{
					throw new ArgumentException("Audio samples must be finite.", nameof(Channels));
				}
			}
		}
	}
}
