using Fractal.Core;

namespace Fractal.Application
{
	public interface IToneAnalysisService
	{
		Task<ToneAnalysisResult> AnalyzeAsync(
			ToneAnalysisRequest request,
			CancellationToken cancellationToken);
	}
}
