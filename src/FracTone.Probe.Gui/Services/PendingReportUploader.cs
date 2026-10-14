namespace FracTone.Probe.Gui.Services
{
	// Stands in until the site's upload API ships: says so, and the report stays saved for later.
	public sealed class PendingReportUploader : IReportUploader
	{
		public Task<ReportUploadResult> UploadAsync(string zipPath, string uploadCode, CancellationToken cancellationToken) =>
			Task.FromResult(new ReportUploadResult(false,
				"Uploading from here comes in the next version. Your report is saved, so you can send it then."));
	}
}
