using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fractal.Companion.Protocol;

namespace FracTone.Probe
{
	[JsonConverter(typeof(JsonStringEnumConverter<ProbeStepKind>))]
	public enum ProbeStepKind
	{
		// Level 1: reads only.
		StatusDump,
		DecodeLayout,
		// Level 2: the edit buffer only.
		SwitchToSafeSlot,
		LoadTemplate,
		VoicingCheck,
		FreshInserts,
		CableCheck,
		// Level 3: opt-in store to the safe slot, put back and verified.
		StoreCheck
	}

	public sealed record ProbeStep(
		string Id,
		int Level,
		ProbeStepKind Kind,
		string Description,
		int? TimeoutMs = null,
		// DecodeLayout: "editBuffer" or "safeSlot".
		string? Source = null,
		// VoicingCheck: the block, effect id and type parameter (the device's own id) whose types are selected.
		string? Block = null,
		int? EffectId = null,
		int? ParameterId = null,
		int? Ordinal = null,
		// Level-2 checks: "ride" or "empty" loads that template into the edit buffer first; otherwise the buffer as it is.
		string? Template = null,
		// Blocks inserted one at a time (01 30 + 01 32), each read back before the next. A voicing check inserts its block
		// only when the buffer lacks it.
		IReadOnlyList<ProbeInsert>? Inserts = null,
		// VoicingCheck: the types to select, each with any words it expects.
		IReadOnlyList<ProbeVoicing>? Types = null,
		// VoicingCheck: the bulk-read words to record (channel A: the parameter id); without it all of channel A.
		IReadOnlyList<int>? Words = null,
		// VoicingCheck: "fm9ModelDefaults" compares every word Fm9ModelDefaults holds for the type (FM9 only).
		string? ExpectFrom = null,
		int? Tolerance = null,
		// CableCheck: the routes sent (01 35), one at a time.
		IReadOnlyList<ProbeRoute>? Routes = null,
		// Expectations carried over from the FM9 by name or id, not yet seen on this device.
		bool? Unverified = null);

	public sealed record ProbeInsert(string Block, int EffectId, int Row, int Column);

	public sealed record ProbeVoicing(int Ordinal, string? Name = null, IReadOnlyDictionary<int, int>? Expect = null);

	// A cable from a block to a row of the next column; the destination's route mask should gain bit (FromRow - 1).
	public sealed record ProbeRoute(int FromRow, int FromColumn, int ToRow, int? ExpectMask = null)
	{
		public string Id => $"r{FromRow}c{FromColumn}>r{ToRow}c{FromColumn + 1}";
	}

	// One device and firmware's probe steps, as data: supporting a new firmware means a new script, not a new program.
	public sealed record ProbeScript(
		string Device,
		int Model,
		string Firmware,
		string? Template,
		// The empty preset (no blocks) fresh inserts and cable checks start from.
		string? EmptyTemplate,
		// Readback rules (Fm9PresetWriteVerifier's, per device): the Amp records, and the Amp words the device may recompute
		// on load. Everything else must come back as sent, apart from body word 0 (the scene playing).
		IReadOnlyList<int> AmpEffectIds,
		IReadOnlyList<int> RecomputedAmpParameters,
		IReadOnlyList<ProbeStep> Steps)
	{
		public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
		{
			WriteIndented = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
		};

		public string Id => $"{Device} {Firmware}";

		public IReadOnlyList<byte[]> LoadTemplate(string? which = null)
		{
			string? template = which switch
			{
				null or "ride" => Template,
				"empty" => EmptyTemplate,
				_ => throw new InvalidOperationException($"The {Id} script names an unknown template '{which}'.")
			};
			if (template is null)
				throw new InvalidOperationException($"The {Id} script has no {which ?? "ride"} template.");
			using Stream stream = typeof(ProbeScript).Assembly.GetManifestResourceStream($"FracTone.Probe.Templates.{template}")
				?? throw new InvalidOperationException($"The template {template} is not bundled.");
			using MemoryStream copy = new();
			stream.CopyTo(copy);
			return ProbeFrames.Split(copy.ToArray());
		}
	}

	public static class ProbeScripts
	{
		private static readonly Lazy<IReadOnlyList<ProbeScript>> Bundled = new(() => Load(typeof(ProbeScripts).Assembly));

		public static IReadOnlyList<ProbeScript> All => Bundled.Value;

		// The script for this firmware, or null: a firmware with no script runs read-only steps only, labelled unverified.
		public static ProbeScript? Find(Gen3Device device, string firmware) =>
			All.FirstOrDefault(script => script.Model == device.Model && script.Firmware == firmware);

		// The read-only steps any firmware of this device may run.
		public static ProbeScript ReadOnly(Gen3Device device, string firmware)
		{
			ProbeScript basis = All.FirstOrDefault(script => script.Model == device.Model)
				?? throw new InvalidOperationException($"No probe script exists for the {device.Name}.");
			return basis with { Firmware = firmware, Template = null, EmptyTemplate = null, Steps = [.. basis.Steps.Where(step => step.Level == 1)] };
		}

		public static ProbeScript Parse(string json) =>
			JsonSerializer.Deserialize<ProbeScript>(json, ProbeScript.Json) ?? throw new InvalidDataException("The probe script is empty.");

		private static IReadOnlyList<ProbeScript> Load(Assembly assembly) =>
			[.. assembly.GetManifestResourceNames()
				.Where(name => name.StartsWith("FracTone.Probe.Scripts.", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
				.Order(StringComparer.Ordinal)
				.Select(name =>
				{
					using Stream stream = assembly.GetManifestResourceStream(name)!;
					using StreamReader reader = new(stream);
					return Parse(reader.ReadToEnd());
				})];
	}
}
