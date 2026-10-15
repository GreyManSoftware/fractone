namespace FracTone.Probe
{
	// CodeProblem marks a missing or refused upload code, so each front end can say how to fix it in its own terms.
	public sealed record ReportUploadResult(bool Accepted, string Message, string? ReportId = null, bool CodeProblem = false);

	// Sends a report zip to the FracTone site with the user's upload code (made on their account page, revocable); see
	// HttpReportUploader. Callers show the summary and file list (ReportUploadPreview) and upload only when the user says so.
	public interface IReportUploader
	{
		Task<ReportUploadResult> UploadAsync(string zipPath, string uploadCode, CancellationToken cancellationToken);
	}
}
