using System.Runtime.CompilerServices;

namespace Fractal.Fm9.Tests
{
	internal static class TestSetup
	{
		// Cab rankings fit cabs in parallel; two at a time keeps this suite light beside the others that run at once.
		[ModuleInitializer]
		internal static void LimitCabFitting() => Fm9CabRanker.MaxParallelism = 2;
	}
}
