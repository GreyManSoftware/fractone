using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FracTone.Probe.Gui.Services;
using FracTone.Probe.Gui.ViewModels;

namespace FracTone.Probe.Gui
{
	public sealed class App : Application
	{
		public override void Initialize() => AvaloniaXamlLoader.Load(this);

		public override void OnFrameworkInitializationCompleted()
		{
			if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
			{
				EngineProbeService probe = new(ReportFolder());
				// One client for the app; the uploader enforces its own per-attempt timeout.
				HttpReportUploader uploader = new(new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
				WizardViewModel wizard = new(probe, uploader, new UploadCodeStore(), new ShellFolderOpener(),
					dispatch: action => Dispatcher.UIThread.Post(action));
				desktop.MainWindow = new MainWindow { DataContext = wizard };
				desktop.Exit += (_, _) => probe.Dispose();
			}
			base.OnFrameworkInitializationCompleted();
		}

		// Reports go where a non-technical user will find them: Documents\FracTone Probe.
		private static string ReportFolder() =>
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FracTone Probe");
	}
}
