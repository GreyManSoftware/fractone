using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace FracTone.Probe.Gui.ViewModels
{
	public abstract class ObservableObject : INotifyPropertyChanged
	{
		public event PropertyChangedEventHandler? PropertyChanged;

		protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
		{
			if (EqualityComparer<T>.Default.Equals(field, value)) return false;
			field = value;
			Changed(name);
			return true;
		}

		protected void Changed([CallerMemberName] string? name = null)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
			OnChanged(name);
		}

		protected virtual void OnChanged(string? name)
		{
		}
	}

	// An ICommand over a sync or async action. Tests await ExecuteAsync; the UI calls Execute.
	public sealed class Command(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
	{
		public Command(Action execute, Func<bool>? canExecute = null)
			: this(() =>
			{
				execute();
				return Task.CompletedTask;
			}, canExecute)
		{
		}

		public event EventHandler? CanExecuteChanged;

		public bool CanExecute(object? parameter = null) => canExecute?.Invoke() ?? true;

		public async void Execute(object? parameter = null) => await ExecuteAsync();

		public Task ExecuteAsync() => CanExecute() ? execute() : Task.CompletedTask;

		public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
	}
}
