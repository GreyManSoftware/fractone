namespace Fractal.Companion.Protocol
{
	public sealed record Fm9OfflinePresetExportRequest(
		string TemplatePath,
		string OutputPath,
		string PresetName,
		IReadOnlyDictionary<Fm9AmpKnob, double> AmpKnobValues);

	public static class Fm9OfflinePresetExporter
	{
		public static void Export(Fm9OfflinePresetExportRequest request)
		{
			ArgumentNullException.ThrowIfNull(request);

			if (string.IsNullOrWhiteSpace(request.TemplatePath))
			{
				throw new ArgumentException("A template .syx path is required.", nameof(request));
			}

			if (string.IsNullOrWhiteSpace(request.OutputPath))
			{
				throw new ArgumentException("An output .syx path is required.", nameof(request));
			}

			string templatePath = Path.GetFullPath(request.TemplatePath);
			string outputPath = Path.GetFullPath(request.OutputPath);

			if (!string.Equals(Path.GetExtension(templatePath), ".syx", StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(Path.GetExtension(outputPath), ".syx", StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException(
					"The template and output paths must both use the .syx extension.",
					nameof(request));
			}

			if (string.Equals(templatePath, outputPath, StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException(
					"The output .syx path must differ from the template .syx path.",
					nameof(request));
			}

			if (!File.Exists(templatePath))
			{
				throw new FileNotFoundException("The FM9 template .syx file was not found.", templatePath);
			}

			string? outputDirectory = Path.GetDirectoryName(outputPath);

			if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
			{
				throw new DirectoryNotFoundException(
					"The output .syx directory does not exist.");
			}

			Fm9PresetDump template = Fm9PresetDump.Parse(File.ReadAllBytes(templatePath));
			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(template);
			byte[] editedBody = body.Bytes.ToArray();

			foreach (KeyValuePair<Fm9AmpKnob, double> value in request.AmpKnobValues)
			{
				Fm9PresetBody currentBody = Fm9PresetBodyDecoder.Decode(
					Fm9PresetBodyEncoder.Reencode(template, editedBody));
				editedBody = Fm9PresetBodyParameterEditor.WithAmpKnob(
					currentBody,
					Fm9AmpChannel.A,
					value.Key,
					value.Value);
			}

			Fm9PresetDump exported = Fm9PresetBodyEncoder.Reencode(template, editedBody)
				.WithPresetName(request.PresetName);
			byte[] exportedBytes = exported.ToArray();
			ValidateExport(exportedBytes, request);
			WriteAtomically(outputPath, exportedBytes);
		}

		private static void ValidateExport(
			ReadOnlySpan<byte> exportedBytes,
			Fm9OfflinePresetExportRequest request)
		{
			Fm9PresetDump exported = Fm9PresetDump.Parse(exportedBytes);

			if (!string.Equals(exported.PresetName, request.PresetName, StringComparison.Ordinal))
			{
				throw new InvalidDataException("The exported FM9 preset name did not validate.");
			}

			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(exported);

			foreach (KeyValuePair<Fm9AmpKnob, double> value in request.AmpKnobValues)
			{
				ushort expected = checked((ushort)Math.Round(
					value.Value / 10 * Fm9PresetBodyParameterEditor.ContinuousWireMaximum,
					MidpointRounding.AwayFromZero));
				ushort actual = Fm9PresetBodyParameterEditor.GetAmpKnobRaw(
					body,
					Fm9AmpChannel.A,
					value.Key);

				if (actual != expected)
				{
					throw new InvalidDataException(
						$"The exported FM9 amp {value.Key} value did not validate.");
				}
			}
		}

		private static void WriteAtomically(string outputPath, ReadOnlySpan<byte> contents)
		{
			string directory = Path.GetDirectoryName(outputPath)!;
			string temporaryPath = Path.Combine(
				directory,
				$".{Path.GetFileName(outputPath)}.{Path.GetRandomFileName()}");

			try
			{
				using (FileStream stream = new(
					temporaryPath,
					FileMode.CreateNew,
					FileAccess.Write,
					FileShare.None,
					4096,
					FileOptions.WriteThrough))
				{
					stream.Write(contents);
					stream.Flush(flushToDisk: true);
				}

				File.Move(temporaryPath, outputPath, overwrite: true);
			}
			finally
			{
				if (File.Exists(temporaryPath))
				{
					File.Delete(temporaryPath);
				}
			}
		}
	}
}
