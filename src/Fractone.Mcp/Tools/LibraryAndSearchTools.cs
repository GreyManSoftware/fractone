using System.ComponentModel;
using Fractal.Fm9;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Fractone.Mcp.Tools
{
	public sealed record Fm9IndexBuildResult(string Index, int Presets, IReadOnlyList<string> Skipped, string? Backup, IReadOnlyList<string> ReadFailures);

	[McpServerToolType]
	public sealed class LibraryTools(Fm9Context context)
	{
		private Fm9BlockLibrary Library => new(context.Settings.LibraryFolder);

		[McpServerTool(Name = "library_list", ReadOnly = true, Idempotent = true, OpenWorld = false)]
		[Description("Lists the user's FM9-Edit block-library favourites (the FM9-Edit library folder), with the model each channel plays and whether a favourite holds all channels or one.")]
		public Task<IReadOnlyList<Fm9LibraryEntry>> List([Description("Only this block type, e.g. \"Amp\".")] string? blockType = null) =>
			Fm9Context.RunSync(() => Library.List(blockType));

		[McpServerTool(Name = "library_save", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
		[Description("Saves a block of a .syx preset as an FM9-Edit favourite in the user's library (FM9-Edit's own file format and folder), all four channels or one. To save what the FM9 is playing, read the edit buffer first.")]
		public Task<Fm9LibraryEntry> Save(
			[Description("The preset file.")] string file,
			[Description("Block, e.g. \"Amp\".")] string block,
			[Description("Favourite name as FM9-Edit lists it.")] string name,
			[Description("One channel (A-D) to save as a single-channel favourite; default all channels.")] string? channel = null) =>
			Fm9Context.RunSync(() => Library.Save(context.Input(file), Fm9BlockRef.Parse(block), name, channel));

		[McpServerTool(Name = "library_apply", ReadOnly = false, Destructive = false, OpenWorld = false)]
		[Description("Applies a favourite to a block of the same type in a .syx preset, into a new file: all channels, or one channel. Scenes and cables are untouched.")]
		public Task<Fm9EditResult> Apply(
			[Description("The favourite's .blk path (from library_list).")] string favourite,
			[Description("The preset file.")] string file,
			[Description("Block in the preset; default the favourite's type, instance 1.")] string? block = null,
			[Description("Channel to write (A-D); default all channels, or the channel a single-channel favourite was saved from.")] string? toChannel = null,
			[Description("Channel of the favourite to take (A-D), for an all-channel favourite applied to one channel.")] string? fromChannel = null,
			[Description("Output file.")] string? output = null) =>
			Fm9Context.RunSync(() =>
			{
				string input = context.Input(file);
				return Fm9BlockLibrary.Apply(context.Input(favourite), input, context.Output(output, input, "favourite"),
					block is null ? null : Fm9BlockRef.Parse(block), toChannel, fromChannel);
			});
	}

	[McpServerToolType]
	public sealed class SearchTools(Fm9Context context)
	{
		[McpServerTool(Name = "index_build", ReadOnly = true, Idempotent = true, OpenWorld = false)]
		[Description("Builds the preset search index, from the FM9 (backs up every preset first: several minutes) or from .syx files and folders (single presets or FM9-Edit / Fractal-Bot bank backups). Stores update the index afterwards.")]
		public Task<Fm9IndexBuildResult> Build(
			[Description("\"fm9\" to read the FM9, or paths of .syx files or folders.")] string[] sources,
			IProgress<ProgressNotificationValue>? progress = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run(async () =>
			{
				if (sources is not { Length: > 0 }) throw new ArgumentException("Give \"fm9\" or at least one path.");
				List<string> paths = [];
				string? backup = null;
				List<string> failures = [];
				foreach (string source in sources)
				{
					if (source.Equals("fm9", StringComparison.OrdinalIgnoreCase))
					{
						Progress<int> report = new(slot => progress?.Report(new ProgressNotificationValue { Progress = slot + 1, Total = 512, Message = $"Read preset {slot}" }));
						Fm9BackupResult read = await context.Device().BackupAsync(0, 511, null, report, cancellationToken);
						backup = read.Folder;
						failures.AddRange(read.Failed);
						paths.Add(read.Folder);
					}
					else
					{
						string path = context.Settings.Resolve(source);
						paths.Add(File.Exists(path) || Directory.Exists(path) ? path : throw new FileNotFoundException($"No file or folder at {path}."));
					}
				}
				Fm9PresetIndex index = Fm9PresetIndex.Build(paths);
				index.Save(context.IndexPath);
				return new Fm9IndexBuildResult(context.IndexPath, index.Presets.Count, index.Skipped, backup, failures);
			});

		[McpServerTool(Name = "search", ReadOnly = true, Idempotent = true, OpenWorld = false)]
		[Description("Searches the preset index: which presets use a model, cab or block, and on which channels and scenes. By default only channels some scene engages. Build the index first with index_build.")]
		public Task<IReadOnlyList<Fm9SearchHit>> Search(
			[Description("Words matched against preset, block and model names, e.g. \"IIC++\" or \"clean 2x12\".")] string? text = null,
			[Description("Block, e.g. \"Cab\" or \"Amp 2\".")] string? block = null,
			[Description("Part of a model or cab name.")] string? model = null,
			[Description("Cab bank number.")] int? cabBank = null,
			[Description("Cab number within its bank.")] int? cabType = null,
			[Description("Also list channels no scene engages.")] bool? includeUnplayed = null) =>
			Fm9Context.RunSync(() =>
			{
				if (!File.Exists(context.IndexPath)) throw new InvalidOperationException("There is no preset index yet; build it with index_build.");
				return Fm9PresetIndex.Load(context.IndexPath).Search(new Fm9IndexQuery
				{
					Text = text, Block = block, Model = model, CabBank = cabBank, CabType = cabType, PlayedOnly = includeUnplayed != true
				});
			});
	}
}
