namespace Fractal.Companion.Protocol
{
	public static class Fm9ProposalPresetExportCommand
	{
		public static Fm9ProposalPresetExportRequest ParseArguments(string[] args)
		{
			ArgumentNullException.ThrowIfNull(args);

			if (args.Length != 5 || !string.Equals(args[0], "--export-fm9-proposal", StringComparison.Ordinal))
			{
				throw new ArgumentException(
					"Usage: --export-fm9-proposal <proposal.json> <template.syx> <output.syx> <preset-name>.");
			}

			return new Fm9ProposalPresetExportRequest(args[1], args[2], args[3], args[4]);
		}
	}
}
