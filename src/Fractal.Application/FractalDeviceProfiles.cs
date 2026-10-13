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
				"FM3 requires its own validated template, parameter mappings, and connection protocol before any generated output or connection is enabled."),
			new(
				FractalDeviceId.AxeFxIii,
				"Axe-Fx III",
				DeviceProfileValidationState.RequiresIndependentValidation,
				DeviceProfileValidationState.RequiresIndependentValidation,
				DeviceProfileValidationState.RequiresIndependentValidation,
				[],
				0,
				0,
				"Axe-Fx III requires its own validated template, parameter mappings, and connection protocol before any generated output or connection is enabled.")
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
