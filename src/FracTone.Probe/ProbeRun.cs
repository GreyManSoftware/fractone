using System.Text.Json.Serialization;

namespace FracTone.Probe
{
	public sealed record ProbeOptions(int Slot, int Level = 1)
	{
		// Level 3 stores to the safe slot, so it needs this too: the CLI sets it only for an explicit --level 3.
		public bool AllowStore { get; init; }
		public TimeSpan Pacing { get; init; } = TimeSpan.FromMilliseconds(90);
		public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(2);
		public TimeSpan DumpTimeout { get; init; } = TimeSpan.FromSeconds(10);
		// The FM9 dropped a load sent straight after a preset change (2026-10-13), so a switch settles before anything else.
		public TimeSpan SwitchSettle { get; init; } = TimeSpan.FromMilliseconds(500);
		public TimeSpan LoadSettle { get; init; } = TimeSpan.FromMilliseconds(300);
		// Which model to ask first, from the port's name; the others are tried after it.
		public byte? ModelHint { get; init; }
		// Step-by-step progress for a GUI: each step's start and finish, and the restore's.
		public IProgress<ProbeProgress>? Progress { get; init; }
	}

	[JsonConverter(typeof(JsonStringEnumConverter<ProbeStepStatus>))]
	public enum ProbeStepStatus
	{
		Passed,
		Failed,
		Skipped,
		Stopped
	}

	[JsonConverter(typeof(JsonStringEnumConverter<ProbeOutcome>))]
	public enum ProbeOutcome
	{
		Completed,
		CompletedWithFailures,
		Stopped,
		Cancelled,
		Crashed
	}

	public sealed record ProbeStepResult(
		string Id,
		string Kind,
		int Level,
		ProbeStepStatus Status,
		string Message,
		DateTimeOffset StartedAt,
		double DurationMs,
		IReadOnlyDictionary<string, string> Data);

	// A preset dump kept exactly as the device sent it (or as we sent it, for a template).
	public sealed record ProbeReadback(string Name, IReadOnlyList<byte[]> Frames, ProbeContent Content)
	{
		public byte[] ToSyx() => [.. Frames.SelectMany(frame => frame)];
	}

	public sealed record ProbeRestore(
		bool Attempted,
		bool LeftAsFound,
		bool? SafeSlotVerified,
		bool? EditBufferVerified,
		bool? SceneVerified,
		bool? StoredSlotRestored,
		IReadOnlyList<string> Notes);

	public sealed class ProbeRun
	{
		public string Tool { get; init; } = "fractone-probe";
		// Named as the site's report upload reads it (with device, firmware and outcome).
		public string ToolVersion { get; init; } = typeof(ProbeRun).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
		public string Os { get; init; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
		public DateTimeOffset StartedAt { get; set; }
		public DateTimeOffset FinishedAt { get; set; }
		public string? Device { get; set; }
		public string? Model { get; set; }
		public string? Firmware { get; set; }
		// False when the firmware has no script of its own: only read-only steps ran, and their results are unverified.
		public bool? FirmwareScripted { get; set; }
		public string? Script { get; set; }
		public int LevelRequested { get; set; }
		public int LevelRun { get; set; }
		public int Slot { get; set; }
		public int? PresetFound { get; set; }
		public string? PresetFoundName { get; set; }
		public int? SceneFound { get; set; }
		public bool? UnsavedEdits { get; set; }
		public ProbeOutcome Outcome { get; set; }
		public string? StopReason { get; set; }
		public List<ProbeStepResult> Steps { get; } = [];
		public ProbeRestore? Restore { get; set; }

		[JsonIgnore]
		public List<ProbeReadback> Readbacks { get; } = [];

		[JsonIgnore]
		public IReadOnlyList<ProbeExchange> Exchanges { get; set; } = [];

		[JsonIgnore]
		public List<string> Log { get; } = [];
	}
}
