using Fractal.Core;

namespace Fractal.Application
{
	public interface IFractalDeviceSession
	{
		Task<DeviceCommandResult> ApplyToEditBufferAsync(
			GeneratedPreset preset,
			CancellationToken cancellationToken);

		Task<DeviceCommandResult> SaveEditBufferAsync(
			int destinationSlot,
			CancellationToken cancellationToken);
	}

	public sealed record DeviceCommandResult(
		bool Succeeded,
		string Message);
}
