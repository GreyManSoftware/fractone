using System.ComponentModel;
using System.Globalization;
using Fractal.Fm9;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Fractone.Mcp.Tools
{
	[McpServerToolType]
	public sealed class SeparateTools(Fm9Context context)
	{
		[McpServerTool(Name = "separate", ReadOnly = true, OpenWorld = true)]
		[Description("Separates the guitar (or another stem) from a full mix with Demucs, for a tone-match target, only for the part to be matched (start and length; 45-60 s is plenty). Only needed when a target is a full mix; an isolated stem or isolated-guitar video needs none. Runs on an NVIDIA GPU (CUDA), an Apple silicon GPU (MPS) or the CPU (half the cores; slower but works). Call with plan: true first and tell the user the device and estimated time, and on first use the install it needs (an optional add-on: Python, PyTorch for the hardware found and Demucs, downloaded into the separatorFolder setting). Writes the stem and the rest of the mix, and reports bleed (bass, kick, cymbals) in the stem.")]
		public Task<object> Separate(
			[Description("The full mix (any common audio file: WAV, AIFF, FLAC, MP3, M4A, OGG, Opus).")] string file,
			[Description("Seconds into the recording where the part starts, default 0.")] double? start = null,
			[Description("Seconds to separate, default 60, at most 180.")] double? length = null,
			[Description("guitar (default), bass, drums, vocals, piano or other.")] string? stem = null,
			[Description("Report the device, estimated time and any first-use install without running.")] bool? plan = null,
			[Description("Run on the CPU even when a GPU is available.")] bool? cpu = null,
			[Description("Passes with shifted copies, 1 (default) to 10: slightly cleaner, proportionally slower.")] int? shifts = null,
			[Description("Output WAV for the stem; default stems/<file> <stem> <start>s.wav.")] string? output = null,
			IProgress<ProgressNotificationValue>? progress = null,
			CancellationToken cancellationToken = default) =>
			Fm9Context.Run<object>(async () =>
			{
				StemSeparator separator = new(context.Settings.SeparatorFolder);
				double seconds = length ?? 60, from = start ?? 0;
				string which = stem ?? "guitar";
				if (plan == true) return await separator.PlanAsync(seconds, shifts ?? 1, cpu == true, cancellationToken);
				string input = context.Input(file);
				string label = string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileNameWithoutExtension(input)} {which} {from:0.#}s");
				string stemPath = output is null ? Path.Combine(context.Settings.WorkFolder, "stems", $"{label}.wav") : context.Settings.Resolve(output);
				string restPath = Path.Combine(Path.GetDirectoryName(stemPath)!, $"{Path.GetFileNameWithoutExtension(stemPath)} rest.wav");
				int step = 0;
				return await separator.SeparateAsync(input, from, seconds, stemPath, restPath, which, shifts ?? 1, cpu == true,
					message => progress?.Report(new ProgressNotificationValue { Progress = ++step, Message = message }), cancellationToken);
			});
	}
}
