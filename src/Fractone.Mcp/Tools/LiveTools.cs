using System.ComponentModel;
using System.Security.Cryptography;
using Fractal.Companion.Protocol;
using Fractal.Fm9;
using ModelContextProtocol.Server;

namespace Fractone.Mcp.Tools
{
	// Shaped like store_preset's outcome, plus the file the buffer was saved to.
	public sealed record Fm9EditBufferStoreOutcome(string Status, string Message, string? Confirmation, Fm9StoreResult? Result, string BufferFile, IReadOnlyList<string> Changes);

	// Live control of what the FM9 is playing, over MIDI, as FM9-Edit does it. These change only the edit buffer (nothing
	// is stored until store_edit_buffer), so they need no confirmation; each reports exactly what changed.
	[McpServerToolType]
	public sealed class LiveTools(Fm9Context context)
	{
		[McpServerTool(Name = "get_parameters", ReadOnly = true, Idempotent = true, OpenWorld = false)]
		[Description("Reads a block's live settings from the FM9 (what is playing now, unsaved edits included): the channel it plays, whether it is bypassed in the current scene, its model, and its settings by name. Defaults to the channel the block is playing.")]
		public Task<Fm9LiveBlockState> GetParameters(
			[Description("Block, e.g. \"Amp\", \"Cab 2\", \"Drive\".")] string block,
			[Description("Channel A-D; default the one playing.")] string? channel = null,
			[Description("Only these settings, e.g. [\"Gain\", \"Master\"]; default all.")] string[]? settings = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(() => context.Live().GetAsync(Fm9BlockRef.Parse(block), channel, settings, cancellationToken));

		[McpServerTool(Name = "set_parameters", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
		[Description("Turns knobs live on the FM9, as FM9-Edit does: sets one or more settings of a block in the edit buffer and reads them back (old -> new). Values are display units or choice names; a model or type by name (\"Amp Model\": \"Brit 800\") makes the FM9 load that model's voicing first, then the other settings go over it. Naming a channel other than the one playing switches the block to it in the current scene. Nothing is stored.")]
		public Task<Fm9LiveResult> SetParameters(
			[Description("Block, e.g. \"Amp\".")] string block,
			[Description("Settings by name, e.g. {\"Gain\": \"6.5\", \"Treble\": \"5\"}.")] Dictionary<string, string> settings,
			[Description("Channel A-D; default the one playing.")] string? channel = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(() => context.Live().SetAsync(Fm9BlockRef.Parse(block), settings, channel, cancellationToken));

		[McpServerTool(Name = "set_block", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
		[Description("Switches a block's channel and/or bypass live, in the current scene (as tapping a block or channel on the FM9 does). Nothing is stored.")]
		public Task<Fm9LiveSwitchResult> SetBlock(
			[Description("Block, e.g. \"Drive\".")] string block,
			[Description("Channel A-D to play.")] string? channel = null,
			[Description("true to bypass, false to engage.")] bool? bypassed = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(() => context.Live().SetBlockAsync(Fm9BlockRef.Parse(block), channel, bypassed, cancellationToken));

		[McpServerTool(Name = "select_scene", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
		[Description("Selects a scene (1-8) of the preset playing. Unsaved edits are kept.")]
		public Task<string> SelectScene([Description("Scene 1-8.")] int scene, CancellationToken cancellationToken = default) =>
			Fm9Context.Run(async () => $"Scene {await context.Live().SelectSceneAsync(scene, cancellationToken)} is playing.");

		[McpServerTool(Name = "store_edit_buffer", Destructive = true, Idempotent = true, OpenWorld = false)]
		[Description("Stores what the FM9 is playing now (the edit buffer, live edits included) into a preset slot, replacing what is there, through the same checked store as store_preset (writable slots, backup, verify, confirm). The buffer is saved to a file first, which is what gets stored. If the result's status is confirm, ask the user and call again with the code.")]
		public Task<Fm9EditBufferStoreOutcome> StoreEditBuffer(
			[Description("Preset slot, 0-511. Usually the preset showing.")] int slot,
			[Description("The code a previous call returned, when writes need confirming.")] string? confirmation = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(async () =>
			{
				Fm9Settings settings = context.Settings;
				if (!settings.Safeguards.AllowsSlot(slot))
					throw new InvalidOperationException($"Preset {slot} is not writable under the current settings (writable slots: {string.Join(", ", settings.Safeguards.WritableSlots)}).");
				Fm9Device device = context.Device();
				Fm9ReadResult read = await device.ReadEditBufferAsync(Path.Combine("live", $"edit buffer {DateTime.Now:yyyyMMdd-HHmmss}.syx"), cancellationToken);
				Fm9PresetDump captured = Fm9PresetDump.Parse(File.ReadAllBytes(read.Path));
				IReadOnlyList<string> changes = await context.StoreSummaryAsync(slot, captured, cancellationToken);
				string action = $"store buffer {Convert.ToHexString(SHA256.HashData(Fm9PresetBodyDecoder.Decode(captured).Bytes.Span))} '{captured.PresetName}' into {slot}";
				if (settings.Safeguards.ConfirmWrites && context.RequireConfirmation(action, confirmation) is string code)
				{
					string current = await device.PeekAsync(slot, cancellationToken);
					return new Fm9EditBufferStoreOutcome("confirm",
						$"This stores what the FM9 is playing ('{captured.PresetName}') into preset {slot}, replacing '{current}'. Ask the user, then call again with confirmation {code}.",
						code, null, read.Path, changes);
				}
				Fm9StoreResult result = await device.StoreCapturedAsync(read.Path, slot, captured, cancellationToken);
				context.Live().ResetHistory();
				return new Fm9EditBufferStoreOutcome("done", $"Stored '{result.Stored}' in preset {slot}, replacing '{result.Replaced}'.", null, result, read.Path, changes);
			});
	}
}
