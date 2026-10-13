using Fractal.Companion.Protocol;

namespace Fractal.Companion.Transport
{
	public interface IFractalMidiTransport
	{
		Task SendAsync(
			FractalSysExFrame frame,
			CancellationToken cancellationToken);

		Task<IReadOnlyList<FractalSysExFrame>> RequestAsync(
			FractalSysExFrame request,
			CancellationToken cancellationToken);

		// Standard MIDI bank select (CC 0, banks of 128) then program change; presets are numbered 0-511 as displayed.
		Task SelectPresetAsync(int presetNumber, int midiChannel, CancellationToken cancellationToken) =>
			throw new NotSupportedException("This transport cannot change presets.");
	}
}
