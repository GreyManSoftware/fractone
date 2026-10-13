using System.ComponentModel;
using Fractal.Fm9;
using ModelContextProtocol.Server;

namespace Fractone.Mcp.Tools
{
	// Offline edits of .syx files. None of them touch the FM9: each writes a new file (never the input) and reads it back.
	// Blocks are named like "Amp", "Amp 2" or "Gate/Expander"; channels are A-D; scenes 1-8.
	[McpServerToolType]
	public sealed class PresetTools(Fm9Context context)
	{
		[McpServerTool(Name = "describe_preset", ReadOnly = true, Idempotent = true, OpenWorld = false)]
		[Description("Describes a .syx preset: its blocks (row, column), what each block channel plays (amp model, cab, type) and which scenes engage it, every scene's channel and bypass per block, and modifiers.")]
		public Task<Fm9PresetSummary> Describe([Description("The preset file.")] string file) =>
			Fm9Context.RunSync(() => Fm9Presets.Describe(context.Input(file)));

		[McpServerTool(Name = "get_channel", ReadOnly = true, Idempotent = true, OpenWorld = false)]
		[Description("Every setting of one block channel by name, as the values FM9-Edit shows (choices as their position; list_block_settings names them).")]
		public Task<IReadOnlyDictionary<string, string>> GetChannel(
			[Description("The preset file.")] string file,
			[Description("Block, e.g. \"Amp\", \"Cab 2\", \"Drive\".")] string block,
			[Description("Channel A-D, default A.")] string? channel = null) =>
			Fm9Context.RunSync(() => Fm9Presets.GetChannel(context.Input(file), Fm9BlockRef.Parse(block), channel ?? "A"));

		[McpServerTool(Name = "list_block_settings", ReadOnly = true, Idempotent = true, OpenWorld = false)]
		[Description("The settings a block type has, with ranges or choices (e.g. every Amp Model name). Use before set_channel.")]
		public Task<IReadOnlyList<Fm9SettingInfo>> ListBlockSettings([Description("Block type, e.g. \"Amp\", \"Delay\".")] string blockType) =>
			Fm9Context.RunSync(() => Fm9Presets.ListSettings(blockType));

		[McpServerTool(Name = "set_channel", ReadOnly = false, Destructive = false, OpenWorld = false)]
		[Description("Writes only the given settings to one block channel, into a new file. Choosing a new model or type (\"Amp Model\": \"USA IIC++\") first loads the voicing the FM9 loads for it, then the given settings go over it. Reports each change read back from the new file.")]
		public Task<Fm9EditResult> SetChannel(
			[Description("The preset file.")] string file,
			[Description("Block, e.g. \"Amp\".")] string block,
			[Description("Channel A-D.")] string channel,
			[Description("Settings by name, e.g. {\"Gain\": \"6.5\", \"Amp Model\": \"USA IIC++\"}.")] Dictionary<string, string> settings,
			[Description("Output file; default edits/<input> (set) <time>.syx.")] string? output = null) =>
			Fm9Context.RunSync(() =>
			{
				string input = context.Input(file);
				return Fm9Presets.SetChannel(input, context.Output(output, input, "set"), Fm9BlockRef.Parse(block), channel, settings);
			});

		[McpServerTool(Name = "copy_channel", ReadOnly = false, Destructive = false, OpenWorld = false)]
		[Description("Copies one block channel onto another channel, from this preset or another preset's same block type, into a new file. Exact word for word when the records match (even a user IR comes across).")]
		public Task<Fm9EditResult> CopyChannel(
			[Description("The preset to change.")] string file,
			[Description("Block in that preset, e.g. \"Amp\".")] string block,
			[Description("Channel to copy from (A-D).")] string from,
			[Description("Channel to copy onto (A-D).")] string to,
			[Description("Preset to copy from; default the same file.")] string? source = null,
			[Description("Block in the source preset, when it differs (e.g. \"Amp 2\").")] string? sourceBlock = null,
			[Description("Output file.")] string? output = null) =>
			Fm9Context.RunSync(() =>
			{
				string input = context.Input(file);
				return Fm9Presets.CopyChannel(input, context.Output(output, input, "copy"), source is null ? input : context.Input(source),
					Fm9BlockRef.Parse(block), from, to, sourceBlock is null ? null : Fm9BlockRef.Parse(sourceBlock));
			});

		[McpServerTool(Name = "compose_scene", ReadOnly = false, Destructive = false, OpenWorld = false)]
		[Description("Sets what one scene plays, into a new file: optionally start as a copy of another scene, then set blocks' channels and bypass states and a name. Blocks not mentioned keep their state.")]
		public Task<Fm9EditResult> ComposeScene(
			[Description("The preset file.")] string file,
			[Description("Scene 1-8 to set.")] int scene,
			[Description("Scene to copy first (1-8).")] int? copyFrom = null,
			[Description("New scene name (up to 31 characters).")] string? name = null,
			[Description("Channels by block, e.g. {\"Amp\": \"B\", \"Cab\": \"B\"}.")] Dictionary<string, string>? channels = null,
			[Description("Bypass by block, e.g. {\"Drive\": true, \"Reverb\": false}.")] Dictionary<string, bool>? bypassed = null,
			[Description("Output file.")] string? output = null) =>
			Fm9Context.RunSync(() =>
			{
				string input = context.Input(file);
				return Fm9Presets.ComposeScene(input, context.Output(output, input, $"scene {scene}"), scene, copyFrom, name, channels, bypassed);
			});

		[McpServerTool(Name = "add_block", ReadOnly = false, Destructive = false, OpenWorld = false)]
		[Description("Adds a block with the FM9's default settings into a grid cell that holds a shunt (it takes that cell's cables), into a new file. describe_preset shows the grid; other routing changes are made on the FM9.")]
		public Task<Fm9EditResult> AddBlock(
			[Description("The preset file.")] string file,
			[Description("Block to add, e.g. \"Delay\", \"Chorus 2\".")] string block,
			[Description("Grid row (1-6).")] int row,
			[Description("Grid column (1-14).")] int column,
			[Description("Output file.")] string? output = null) =>
			Fm9Context.RunSync(() =>
			{
				string input = context.Input(file);
				return Fm9Presets.AddBlock(input, context.Output(output, input, "add"), Fm9BlockRef.Parse(block), row, column);
			});

		[McpServerTool(Name = "remove_blocks", ReadOnly = false, Destructive = false, OpenWorld = false)]
		[Description("Removes blocks as the FM9 does, into a new file: a shunt takes each block's cell so every cable stays, and scenes stop listing it.")]
		public Task<Fm9EditResult> RemoveBlocks(
			[Description("The preset file.")] string file,
			[Description("Blocks to remove, e.g. [\"Reverb\", \"Delay 2\"].")] string[] blocks,
			[Description("Output file.")] string? output = null) =>
			Fm9Context.RunSync(() =>
			{
				string input = context.Input(file);
				return Fm9Presets.RemoveBlocks(input, context.Output(output, input, "remove"), blocks.Select(Fm9BlockRef.Parse).ToArray());
			});

		[McpServerTool(Name = "rename_preset", ReadOnly = false, Destructive = false, OpenWorld = false)]
		[Description("Renames a preset (up to 31 printable characters), into a new file.")]
		public Task<Fm9EditResult> RenamePreset(
			[Description("The preset file.")] string file,
			[Description("New name.")] string name,
			[Description("Output file.")] string? output = null) =>
			Fm9Context.RunSync(() =>
			{
				string input = context.Input(file);
				return Fm9Presets.Rename(input, context.Output(output, input, "renamed"), name);
			});
	}
}
