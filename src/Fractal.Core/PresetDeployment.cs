namespace Fractal.Core
{
	public enum PresetDeploymentMode
	{
		Audition,
		SaveToPresetSlot
	}

	public sealed record GeneratedPreset(
		string Name,
		IReadOnlyList<GeneratedBlock> Blocks);

	public sealed record GeneratedBlock(
		string BlockType,
		IReadOnlyDictionary<string, string> Parameters);

	public sealed record PresetDeploymentRequest(
		GeneratedPreset Preset,
		PresetDeploymentMode Mode,
		int? DestinationSlot)
	{
		public void Validate()
		{
			ArgumentNullException.ThrowIfNull(Preset);

			if (string.IsNullOrWhiteSpace(Preset.Name))
			{
				throw new InvalidOperationException("A generated preset must have a name.");
			}

			if (Mode == PresetDeploymentMode.Audition && DestinationSlot is not null)
			{
				throw new InvalidOperationException(
					"Audition mode writes only to the FM9 edit buffer and cannot specify a preset slot.");
			}

			if (Mode == PresetDeploymentMode.SaveToPresetSlot && DestinationSlot is null)
			{
				throw new InvalidOperationException(
					"Saving a preset requires an explicit destination slot.");
			}

			if (DestinationSlot is < 0)
			{
				throw new ArgumentOutOfRangeException(
					nameof(DestinationSlot),
					"Preset slots cannot be negative.");
			}
		}
	}
}
