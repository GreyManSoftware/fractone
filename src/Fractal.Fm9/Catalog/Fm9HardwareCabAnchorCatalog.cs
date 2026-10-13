using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fractal.Companion.Catalog
{
	public sealed record Fm9HardwareCabAnchor(
		[property: JsonPropertyName("bank")] int Bank,
		[property: JsonPropertyName("ordinal")] int Ordinal,
		[property: JsonPropertyName("renderedName")] string RenderedName,
		[property: JsonPropertyName("bulkSha256")] string BulkSha256,
		[property: JsonPropertyName("rawEvidenceSha256")] string RawEvidenceSha256,
		[property: JsonPropertyName("baselineRestored")] bool BaselineRestored,
		[property: JsonPropertyName("fullPresetBaselineRestored")] bool FullPresetBaselineRestored);

	public sealed record Fm9HardwareCabAnchorDocument(
		[property: JsonPropertyName("schemaVersion")] int SchemaVersion,
		[property: JsonPropertyName("firmwareVersion")] string FirmwareVersion,
		[property: JsonPropertyName("source")] string Source,
		[property: JsonPropertyName("anchors")] IReadOnlyList<Fm9HardwareCabAnchor> Anchors);

	public sealed class Fm9HardwareCabAnchorCatalog
	{
		public Fm9HardwareCabAnchorCatalog(Fm9HardwareCabAnchorDocument document)
		{
			ArgumentNullException.ThrowIfNull(document);

			if (document.SchemaVersion != 1 ||
				document.FirmwareVersion != "12.00" ||
				string.IsNullOrWhiteSpace(document.Source) ||
				document.Anchors is null ||
				document.Anchors.Count == 0 ||
				document.Anchors.Any(anchor =>
					anchor.Bank is not (0 or 1 or 3) ||
					anchor.Ordinal < 0 ||
					anchor.Ordinal >= (anchor.Bank == 3 ? 189 : 1024) ||
					string.IsNullOrWhiteSpace(anchor.RenderedName) ||
					!IsSha256(anchor.BulkSha256) ||
					!IsSha256(anchor.RawEvidenceSha256) ||
					!anchor.BaselineRestored ||
					!anchor.FullPresetBaselineRestored) ||
				document.Anchors.Select(anchor => (anchor.Bank, anchor.Ordinal))
					.Distinct().Count() != document.Anchors.Count)
			{
				throw new InvalidDataException("The FM9 Cab anchor catalog is invalid.");
			}

			Document = document;
		}

		public Fm9HardwareCabAnchorDocument Document { get; }

		public Fm9HardwareCabAnchor ResolveAnchor(int bank, int ordinal)
		{
			return Document.Anchors.SingleOrDefault(anchor =>
					anchor.Bank == bank && anchor.Ordinal == ordinal)
				?? throw new KeyNotFoundException(
					$"No exact hardware anchor exists for FM9 Cab bank {bank}, ordinal {ordinal}.");
		}

		public static async Task<Fm9HardwareCabAnchorCatalog> LoadBundledAsync(
			CancellationToken cancellationToken = default)
		{
			Assembly assembly = typeof(Fm9HardwareCabAnchorCatalog).Assembly;
			await using Stream stream = assembly.GetManifestResourceStream(
				"Fractal.Companion.Catalog.fm9-12-hardware-cab-anchors.json")
				?? throw new InvalidOperationException(
					"The bundled FM9 Cab anchor catalog is unavailable.");
			Fm9HardwareCabAnchorDocument document =
				await JsonSerializer.DeserializeAsync<Fm9HardwareCabAnchorDocument>(
					stream,
					cancellationToken: cancellationToken)
				?? throw new InvalidDataException("The FM9 Cab anchor catalog is empty.");
			return new Fm9HardwareCabAnchorCatalog(document);
		}

		private static bool IsSha256(string? value)
		{
			return value is { Length: 64 } && value.All(Uri.IsHexDigit);
		}
	}
}
