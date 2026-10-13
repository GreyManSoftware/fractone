using System.ComponentModel;
using Fractal.Fm9;
using ModelContextProtocol.Server;

namespace Fractone.Mcp.Tools
{
	public sealed record Fm9HistoryView(int Preset, string PresetName, IReadOnlyList<string> Steps, IReadOnlyList<string> Snapshots);

	[McpServerToolType]
	public sealed class HistoryTools(Fm9Context context)
	{
		[McpServerTool(Name = "diff", ReadOnly = true, Idempotent = true, OpenWorld = false)]
		[Description("Compares two presets and lists what differs, as a player reads it: settings by block and channel in display units (\"Amp 1 A: Bass 1.75 -> 1.5\"), scenes (channel played, bypass, names), the grid (blocks added, removed, moved) and modifiers. Each side is a .syx file, a slot number, \"live\" (what the FM9 is playing, unsaved edits included) or \"last-load\". Use for \"what did I change\" (a slot vs live) or \"mine vs 003\".")]
		public Task<Fm9DiffResult> Diff(
			[Description("The first preset: a file, a slot number, \"live\" or \"last-load\".")] string a,
			[Description("The second preset, the same way.")] string b,
			[Description("Hide what cannot change the sound: names, and channels no scene plays.")] bool? soundOnly = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(async () =>
			{
				(Fractal.Companion.Protocol.Fm9UploadedPreset first, string firstLabel) = await context.PresetAsync(a, cancellationToken);
				(Fractal.Companion.Protocol.Fm9UploadedPreset second, string secondLabel) = await context.PresetAsync(b, cancellationToken);
				return Fm9Diff.Compare(first, second, soundOnly == true, firstLabel, secondLabel);
			});

		[McpServerTool(Name = "history", ReadOnly = true, Idempotent = true, OpenWorld = false)]
		[Description("The live edits made to the preset showing (newest first) that undo can take back, and the named snapshots. History starts again when another preset is selected, a preset is auditioned, or the edits are stored.")]
		public Task<Fm9HistoryView> History() =>
			Fm9Context.RunSync(() =>
			{
				Fm9LiveHistoryState state = context.Live().History();
				return new Fm9HistoryView(state.Preset, state.PresetName,
					state.Steps.Select((step, index) => $"{index + 1}. {step.At:HH:mm:ss} {step.Description}").ToArray(),
					state.Snapshots.Select(snapshot => $"{snapshot.Name} (preset {snapshot.Preset} '{snapshot.PresetName}', {snapshot.At:yyyy-MM-dd HH:mm})").ToArray());
			});

		[McpServerTool(Name = "undo", ReadOnly = false, Destructive = false, OpenWorld = false)]
		[Description("Takes back the last live edits on the preset showing, newest first: settings written back exactly, channel and bypass switched back, and a model change undone by restoring the edit buffer from just before it. Nothing is stored.")]
		public Task<IReadOnlyList<string>> Undo(
			[Description("How many edits to take back, default 1.")] int? steps = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(() => context.Live().UndoAsync(steps ?? 1, cancellationToken));

		[McpServerTool(Name = "snapshot", ReadOnly = true, OpenWorld = false)]
		[Description("Keeps the whole edit buffer (what the FM9 is playing now) under a name, to come back to with restore_snapshot. A snapshot with the same name is replaced.")]
		public Task<Fm9LiveSnapshot> Snapshot(
			[Description("A name, e.g. \"before mids\".")] string name,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(() => context.Live().SnapshotAsync(name, cancellationToken));

		[McpServerTool(Name = "restore_snapshot", ReadOnly = false, Destructive = false, OpenWorld = false)]
		[Description("Puts a named snapshot back into the edit buffer. The buffer it replaces is recorded first, so undo takes the restore back. Nothing is stored.")]
		public Task<string> RestoreSnapshot(
			[Description("The snapshot's name.")] string name,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(() => context.Live().RestoreSnapshotAsync(name, cancellationToken));
	}
}
