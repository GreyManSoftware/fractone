using System.ComponentModel;
using Fractal.Companion.Protocol;
using Fractal.Fm9;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Fractone.Mcp.Tools
{
	public sealed record Fm9BridgeStatus(string? Device, Fm9Status? Fm9, string? Fm9Error, IReadOnlyDictionary<string, string> Support,
		Fm9MidiPorts? Ports, string SettingsFile, string WorkFolder, string LibraryFolder, Fm9Safeguards Safeguards, string Dictionary, string Version);

	// Changes: what the store changes in the slot (a diff summary), read before anything is written.
	public sealed record Fm9StoreOutcome(string Status, string Message, string? Confirmation, Fm9StoreResult? Result, IReadOnlyList<string> Changes);

	[McpServerToolType]
	public sealed class DeviceTools(Fm9Context context)
	{
		[McpServerTool(Name = "status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
		[Description("The device connected and the devices FracTone supports, the FM9's firmware and the preset and scene showing, the MIDI ports, the settings in force (work folder, FM9-Edit library folder, safeguards) and the server version. Call this first.")]
		public async Task<Fm9BridgeStatus> Status(CancellationToken cancellationToken = default)
		{
			Fm9Settings settings = context.Settings;
			Fm9Status? status = null;
			string? error = null;
			Fm9MidiPorts? ports = null;
			try { ports = Fm9Connection.ListPorts(); }
			catch (PlatformNotSupportedException exception) { error = exception.Message; }
			try { status = await context.Device().StatusAsync(cancellationToken); }
			catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or PlatformNotSupportedException)
			{
				error = exception.Message;
			}
			string dictionary;
			try { _ = Fractal.Companion.Catalog.Fm9EffectDefinitions.Bundled; dictionary = Fractal.Companion.Catalog.Fm9EffectDefinitions.BundledSource; }
			catch (InvalidOperationException exception) { dictionary = exception.Message; }
			return new(status is null ? null : "FM9", status, error, Support, ports, context.SettingsPath, settings.WorkFolder, settings.LibraryFolder, settings.Safeguards, dictionary,
				typeof(DeviceTools).Assembly.GetName().Version?.ToString(3) ?? "0");
		}

		// What FracTone supports, device by device.
		private static readonly IReadOnlyDictionary<string, string> Support = new Dictionary<string, string>
		{
			["FM9"] = "supported (firmware 12; writes refused on other firmware)",
			["Axe-Fx III"] = "planned",
			["FM3"] = "planned"
		};

		[McpServerTool(Name = "read_preset", ReadOnly = true, Idempotent = true, OpenWorld = false)]
		[Description("Reads a stored preset (0-511) from the FM9 into a .syx file and describes its blocks, channels and scenes. Nothing on the FM9 changes.")]
		public Task<Fm9ReadResult> ReadPreset(
			[Description("Preset slot, 0-511.")] int slot,
			[Description("Where to save it (relative paths are in the work folder). Default: reads/<slot> <name> <time>.syx.")] string? file = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(() => context.Device().ReadPresetAsync(slot, file, cancellationToken));

		[McpServerTool(Name = "read_edit_buffer", ReadOnly = true, Idempotent = true, OpenWorld = false)]
		[Description("Saves what the FM9 is playing now (the edit buffer, including unsaved changes) to a .syx file and describes it.")]
		public Task<Fm9ReadResult> ReadEditBuffer(
			[Description("Where to save it. Default: reads/edit buffer <time> <name>.syx.")] string? file = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(() => context.Device().ReadEditBufferAsync(file, cancellationToken));

		[McpServerTool(Name = "backup", ReadOnly = true, Idempotent = true, OpenWorld = false)]
		[Description("Backs up a range of presets from the FM9, one .syx per slot, into a folder (default backups/full <time>). A full backup of 512 presets takes several minutes. Erased slots are listed, not written.")]
		public Task<Fm9BackupResult> Backup(
			[Description("First slot, default 0.")] int? first = null,
			[Description("Last slot, default 511.")] int? last = null,
			[Description("Folder for the files (relative to the work folder).")] string? folder = null,
			IProgress<ProgressNotificationValue>? progress = null,
			CancellationToken cancellationToken = default)
		{
			int from = first ?? 0, to = last ?? 511;
			Progress<int> report = new(slot => progress?.Report(new ProgressNotificationValue
			{
				Progress = slot - from + 1, Total = to - from + 1, Message = $"Read preset {slot}"
			}));
			return Fm9Context.Run(() => context.Device().BackupAsync(from, to, folder, report, cancellationToken));
		}

		[McpServerTool(Name = "select_preset", Destructive = true, Idempotent = true, OpenWorld = false)]
		[Description("Shows a preset and scene on the FM9. Unsaved edits to the preset showing are lost, as when switching on the FM9 itself; with the refuseLoadIfBufferChanged safeguard on, it is refused while there are any. To change scene within the preset playing, use select_scene.")]
		public Task<string> SelectPreset(
			[Description("Preset slot, 0-511.")] int slot,
			[Description("Scene 1-8, default 1.")] int? scene = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(async () =>
			{
				await context.Device().SelectAsync(slot, scene ?? 1, cancellationToken);
				context.Live().ResetHistory();
				return $"Preset {slot}, scene {scene ?? 1} is showing.";
			});

		[McpServerTool(Name = "audition", Destructive = true, Idempotent = true, OpenWorld = false)]
		[Description("Loads a .syx into the FM9's edit buffer to play it. Nothing is stored: switching presets on the FM9 discards it. Replaces what the edit buffer held, including unsaved edits (refused while refuseLoadIfBufferChanged is on and the buffer has edits nobody stored).")]
		public Task<Fm9AuditionResult> Audition(
			[Description("The preset file.")] string file,
			[Description("Select this preset first (its own unsaved edits are lost); default: load into the preset showing.")] int? slot = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(async () =>
			{
				Fm9AuditionResult result = await context.Device().AuditionAsync(context.Input(file), slot, cancellationToken);
				context.Live().ResetHistory();
				return result;
			});

		[McpServerTool(Name = "store_preset", Destructive = true, Idempotent = true, OpenWorld = false)]
		[Description("Stores a .syx into a preset slot on the FM9, replacing what is there, and reports what it replaced. Also restores a backup (store the backup file). The import is checked before storing and undone if it fails. Safeguards from the settings apply: writable slots, keeping a backup, verifying the stored copy, refusing over unsaved edits, and confirmWrites (then the first call returns a confirmation code; call again with it).")]
		public Task<Fm9StoreOutcome> StorePreset(
			[Description("The preset file to store.")] string file,
			[Description("Preset slot, 0-511.")] int slot,
			[Description("The code a previous call returned, when writes need confirming.")] string? confirmation = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(async () =>
			{
				string path = context.Input(file);
				Fm9PresetDump dump = Fm9PresetDump.Parse(File.ReadAllBytes(path));
				string action = $"store {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))} into {slot}";
				Fm9Device device = context.Device();
				if (!context.Settings.Safeguards.AllowsSlot(slot))
					throw new McpException($"Preset {slot} is not writable under the current settings (writable slots: {string.Join(", ", context.Settings.Safeguards.WritableSlots)}).");
				IReadOnlyList<string> changes = await context.StoreSummaryAsync(slot, dump, cancellationToken);
				if (context.Settings.Safeguards.ConfirmWrites)
				{
					string? code = context.RequireConfirmation(action, confirmation);
					if (code is not null)
					{
						string current = await device.PeekAsync(slot, cancellationToken);
						return new Fm9StoreOutcome("confirm",
							$"This replaces preset {slot} ('{current}') with '{dump.PresetName}' (changes listed). Ask the user, then call again with confirmation {code}.", code, null, changes);
					}
				}
				Fm9StoreResult result = await device.StoreAsync(path, slot, cancellationToken);
				UpdateIndex(slot, dump, path);
				context.Live().ResetHistory();
				return new Fm9StoreOutcome("done", $"Stored '{result.Stored}' in preset {slot}, replacing '{result.Replaced}'.", null, result, changes);
			});

		// A stored preset replaces its slot in the search index, when there is one.
		private void UpdateIndex(int slot, Fm9PresetDump dump, string source)
		{
			if (!File.Exists(context.IndexPath)) return;
			try
			{
				Fm9PresetIndex index = Fm9PresetIndex.Load(context.IndexPath);
				index.Upsert(Fm9PresetIndex.Index(slot, dump, source));
				index.Save(context.IndexPath);
			}
			catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or IOException or System.Text.Json.JsonException) { }
		}
	}
}
