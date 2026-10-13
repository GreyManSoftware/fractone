using Fractal.Application;
using System.Globalization;

namespace Fractal.Companion.Protocol
{
	public static class Fm9ProfileParameterSerializer
	{
		public static Fm9PresetDump Apply(
			Fm9PresetDump template,
			Fm9BlockRecordTemplateProfile profile,
			ToneDesignProposal proposal,
			string presetName)
		{
			ArgumentNullException.ThrowIfNull(template);
			ArgumentNullException.ThrowIfNull(profile);
			ArgumentNullException.ThrowIfNull(proposal);

			if (string.IsNullOrWhiteSpace(presetName))
			{
				throw new ArgumentException("An FM9 preset name is required.", nameof(presetName));
			}

			byte[] bytes = Fm9PresetBodyDecoder.Decode(template).Bytes.ToArray();

			foreach (ToneDesignBlockProposal block in proposal.Blocks)
			{
				foreach (KeyValuePair<string, string> parameter in block.Parameters)
				{
					if (!double.TryParse(
						parameter.Value,
						NumberStyles.Float,
						CultureInfo.InvariantCulture,
						out double value))
					{
						throw new InvalidDataException(
							$"Parameter '{block.BlockType} {block.Instance} {parameter.Key}' must be an invariant numeric value.");
					}

					Fm9PresetBody current = Fm9PresetBodyDecoder.Decode(
						Fm9PresetBodyEncoder.Reencode(template, bytes));
					bytes = Fm9PresetBodyParameterEditor.WithProfileParameter(
						current,
						profile,
						block.BlockType,
						block.Instance,
						parameter.Key,
						value);
				}
			}

			Fm9PresetDump exported = Fm9PresetBodyEncoder.Reencode(template, bytes)
				.WithPresetName(presetName);
			Fm9PresetDump reparsed = Fm9PresetDump.Parse(exported.ToArray());
			_ = Fm9PresetBodyDecoder.Decode(reparsed);
			return reparsed;
		}
	}
}
