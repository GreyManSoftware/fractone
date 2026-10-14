namespace FracTone.Probe
{
	public sealed record ReportUploadResult(bool Accepted, string Message, string? ReportId = null);

	// Sends a report zip to the FracTone site with the user's upload code (made on their account page, revocable); see
	// HttpReportUploader. Callers show the summary and file list (ReportUploadPreview) and upload only when the user says so.
	public interface IReportUploader
	{
		Task<ReportUploadResult> UploadAsync(string zipPath, string uploadCode, CancellationToken cancellationToken);
	}
}
