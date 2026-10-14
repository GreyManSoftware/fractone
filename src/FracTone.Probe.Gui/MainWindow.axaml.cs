using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FracTone.Probe.Gui.ViewModels;

namespace FracTone.Probe.Gui
{
	public sealed partial class MainWindow : Window
	{
		private bool _closingAfterRestore;

		public MainWindow()
		{
			AvaloniaXamlLoader.Load(this);
		}

		// Closing mid-run cancels and waits for the engine's restore, so the window never leaves the device half-changed.
		protected override async void OnClosing(WindowClosingEventArgs e)
		{
			base.OnClosing(e);
			if (_closingAfterRestore || DataContext is not WizardViewModel { IsRunning: true, RunTask: Task run } wizard)
				return;
			e.Cancel = true;
			wizard.CancelCommand.Execute(null);
			try
			{
				await run;
			}
			finally
			{
				_closingAfterRestore = true;
				Close();
			}
		}
	}
}
