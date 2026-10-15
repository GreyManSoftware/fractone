namespace Fractal.Application
{
	public enum FractalDeviceId
	{
		Fm9,
		Fm3,
		AxeFxIii
	}

	public enum DeviceProfileValidationState
	{
		Verified,
		RequiresIndependentValidation
	}

	public sealed record FractalDeviceProfile(
		FractalDeviceId Id,
		string DisplayName,
		DeviceProfileValidationState TemplateState,
		DeviceProfileValidationState ParameterMappingState,
		DeviceProfileValidationState ConnectionState,
		IReadOnlyList<string> VerifiedFirmwareProfiles,
		int InputBlockCount,
		int OutputBlockCount,
		string ValidationRequirement)
	{
		public bool IsOutputEligible =>
			TemplateState == DeviceProfileValidationState.Verified &&
			ParameterMappingState == DeviceProfileValidationState.Verified &&
			ConnectionState == DeviceProfileValidationState.Verified;

		// Generation is offered as a beta: presets are translated from the FM9's vocabulary by an exporter nobody has
		// proved on this hardware yet, so the profile stays unverified and every output says so.
		public bool IsBeta { get; init; }

		// Whether the designer may generate and edit presets for this device (verified, or as a labelled beta).
		public bool IsGenerationEligible => IsOutputEligible || IsBeta;

		// How the device is named in choices and file names: betas carry their label so it can't be missed.
		public string ChoiceLabel => IsBeta ? $"{DisplayName} (Beta: unverified on hardware)" : DisplayName;
	}

	public sealed class FractalDeviceProfileCatalog
	{
		public const string Fm9Firmware12RideBaselineProfile = "fm9-firmware-12-ride-baseline";

		private static readonly IReadOnlyList<FractalDeviceProfile> Profiles =
		[
			new(
				FractalDeviceId.Fm9,
				"FM9",
				DeviceProfileValidationState.Verified,
				DeviceProfileValidationState.Verified,
				DeviceProfileValidationState.Verified,
				[Fm9Firmware12RideBaselineProfile],
				// In 1-3 and USB; Out 1-3 (FracCompat, 2026-10-08).
				4,
				3,
				"Verified for the FM9 firmware-12 Ride baseline: catalog mappings, bounded template serialization, and volatile edit-buffer connection eligibility."),
			new(
				FractalDeviceId.Fm3,
				"FM3",
				DeviceProfileValidationState.RequiresIndependentValidation,
				DeviceProfileValidationState.RequiresIndependentValidation,
				DeviceProfileValidationState.RequiresIndependentValidation,
				[],
				0,
				0,
				"FM3 generation is a beta, unverified on hardware: presets are translated from the FM9 template and must be checked on the FM3 before use.")
			{
				IsBeta = true
			},
			new(
				FractalDeviceId.AxeFxIii,
				"Axe-Fx III",
				DeviceProfileValidationState.RequiresIndependentValidation,
				DeviceProfileValidationState.RequiresIndependentValidation,
				DeviceProfileValidationState.RequiresIndependentValidation,
				[],
				0,
				0,
				"Axe-Fx III generation is a beta, unverified on hardware: presets are translated from the FM9 template and must be checked on the Axe-Fx III before use.")
			{
				IsBeta = true
			}
		];

		public IReadOnlyList<FractalDeviceProfile> GetAll()
		{
			return Profiles;
		}

		public FractalDeviceProfile? Find(string? displayName)
		{
			return Profiles.SingleOrDefault(profile =>
				string.Equals(profile.DisplayName, displayName, StringComparison.Ordinal));
		}

		public FractalDeviceProfile GetRequired(FractalDeviceId id)
		{
			return Profiles.Single(profile => profile.Id == id);
		}
	}
}
