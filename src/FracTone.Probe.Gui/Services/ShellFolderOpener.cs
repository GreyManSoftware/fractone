using System.Diagnostics;

namespace FracTone.Probe.Gui.Services
{
	public sealed class ShellFolderOpener : IFolderOpener
	{
		public void Open(string folder) =>
			Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true })?.Dispose();
	}
}
