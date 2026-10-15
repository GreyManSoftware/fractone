using System.Globalization;
using FracTone.Probe.Gui.Services;

namespace FracTone.Probe.Gui.ViewModels
{
	public enum WizardPage
	{
		Welcome,
		Connect,
		SafeSlot,
		Level,
		Run,
		Done
	}

	public enum CheckLevel
	{
		Quick = 1,
		Full = 2,
		StoreCheck = 3
	}

	// The probe wizard: before you start, connect, safe slot, level, run, done. All device work goes through the engine
	// (IProbeService); this only orders the pages, checks the user's answers and words the results plainly.
	public sealed class WizardViewModel : ObservableObject, IProgress<ProbeProgress>
	{
		private static readonly string[] PageProperties =
			[nameof(IsWelcome), nameof(IsConnect), nameof(IsSafeSlot), nameof(IsLevel), nameof(IsRun), nameof(IsDone),
			nameof(StepTitle), nameof(NextText), nameof(ShowNavigation), nameof(IsRunning)];

		private readonly IProbeService _probe;
		private readonly IReportUploader _uploader;
		private readonly UploadCodeStore _codes;
		private readonly IFolderOpener _folders;
		private readonly Action<Action> _dispatch;
		private readonly Func<DateTimeOffset> _now;
		private readonly Command[] _commands;
		private CancellationTokenSource? _cancel;
		private DateTimeOffset _runStarted;

		private WizardPage _page;
		private bool _editorsClosed;
		private bool _disclaimerAccepted;
		private bool _busy;
		private ProbeIdentity? _identity;
		private string? _connectError;
		private string _slotText = "";
		private int? _checkedSlot;
		private string? _slotName;
		private string? _slotError;
		private bool _slotConfirmed;
		private CheckLevel _level = CheckLevel.Quick;
		private bool _storeAcknowledged;
		private double _progressPercent;
		private string _currentStep = "";
		private string _timeLeft = "";
		private bool _cancelling;
		private ProbeRun? _run;
		private ProbeReportFiles? _report;
		private string? _reportError;
		private string _uploadCode = "";
		private bool _hasSavedCode;
		private string? _codeStatus;
		private ReportUploadPreview? _uploadPreview;
		private string? _uploadStatus;

		public WizardViewModel(IProbeService probe, IReportUploader uploader, UploadCodeStore codes, IFolderOpener folders,
			Action<Action>? dispatch = null, Func<DateTimeOffset>? now = null)
		{
			_probe = probe;
			_uploader = uploader;
			_codes = codes;
			_hasSavedCode = codes.Load() is not null;
			_folders = folders;
			_dispatch = dispatch ?? (action => action());
			_now = now ?? (() => DateTimeOffset.UtcNow);
			NextCommand = new Command(NextAsync, CanGoNext);
			BackCommand = new Command(Back, () => !_busy && _page is WizardPage.Connect or WizardPage.SafeSlot or WizardPage.Level);
			ConnectCommand = new Command(ConnectAsync, () => !_busy && _page == WizardPage.Connect);
			CheckSlotCommand = new Command(CheckSlotAsync, () => !_busy && _page == WizardPage.SafeSlot && _slotText.Trim().Length > 0);
			CancelCommand = new Command(Cancel, () => _page == WizardPage.Run && _cancel is not null && !_cancelling);
			SaveCodeCommand = new Command(SaveCode, () => !_busy && _uploadCode.Trim().Length > 0);
			ReviewUploadCommand = new Command(ReviewUpload, () => !_busy && _report is not null && _hasSavedCode && _uploadPreview is null);
			UploadCommand = new Command(UploadAsync, () => !_busy && _uploadPreview is not null);
			DontUploadCommand = new Command(DontUpload, () => !_busy && _uploadPreview is not null);
			OpenFolderCommand = new Command(() => _folders.Open(Path.GetDirectoryName(_report!.Zip)!), () => _report is not null);
			_commands = [NextCommand, BackCommand, ConnectCommand, CheckSlotCommand, CancelCommand, SaveCodeCommand, ReviewUploadCommand,
				UploadCommand, DontUploadCommand, OpenFolderCommand];
		}

		public Command NextCommand { get; }
		public Command BackCommand { get; }
		public Command ConnectCommand { get; }
		public Command CheckSlotCommand { get; }
		public Command CancelCommand { get; }
		public Command SaveCodeCommand { get; }
		public Command ReviewUploadCommand { get; }
		public Command UploadCommand { get; }
		public Command DontUploadCommand { get; }
		public Command OpenFolderCommand { get; }

		// The run in progress, so closing the window can cancel it and wait for the restore.
		public Task? RunTask { get; private set; }

		public WizardPage Page
		{
			get => _page;
			private set
			{
				if (!Set(ref _page, value)) return;
				foreach (string name in PageProperties)
					Changed(name);
			}
		}

		public bool IsWelcome => _page == WizardPage.Welcome;
		public bool IsConnect => _page == WizardPage.Connect;
		public bool IsSafeSlot => _page == WizardPage.SafeSlot;
		public bool IsLevel => _page == WizardPage.Level;
		public bool IsRun => _page == WizardPage.Run;
		public bool IsDone => _page == WizardPage.Done;
		public bool ShowNavigation => _page is not (WizardPage.Run or WizardPage.Done);
		public string StepTitle => $"Step {(int)_page + 1} of 6";
		public string NextText => _page == WizardPage.Level ? "Start" : "Next";
		public bool IsRunning => _page == WizardPage.Run;

		public bool IsBusy
		{
			get => _busy;
			private set => Set(ref _busy, value);
		}

		// Page 1: before you start.
		public bool EditorsClosed
		{
			get => _editorsClosed;
			set => Set(ref _editorsClosed, value);
		}

		// Also page 1: the disclaimer, which must be ticked before anything touches the device.
		public string DisclaimerText => ProbeDisclaimer.Notice;

		public bool DisclaimerAccepted
		{
			get => _disclaimerAccepted;
			set => Set(ref _disclaimerAccepted, value);
		}

		// Page 2: connect.
		public ProbeIdentity? Identity
		{
			get => _identity;
			private set
			{
				if (!Set(ref _identity, value)) return;
				Changed(nameof(DeviceSummary));
				Changed(nameof(ScriptedSummary));
				Changed(nameof(IsConnected));
				Changed(nameof(SlotHint));
				Changed(nameof(CanUseFull));
			}
		}

		public bool IsConnected => _identity is not null;
		public string? DeviceSummary => _identity is null ? null : $"Found your {_identity.Device.Name}, firmware {_identity.Firmware}.";

		public string? ScriptedSummary => _identity is null ? null : _identity.Scripted
			? "This firmware is fully supported: every check is available."
			: "We don't have checks written for this firmware yet, so only the quick look-only check can run. That still helps.";

		public string? ConnectError
		{
			get => _connectError;
			private set => Set(ref _connectError, value);
		}

		// Page 3: safe slot.
		public string SlotHint => _identity is null ? "" : $"Type a preset number from 0 to {_identity.Presets - 1}.";

		public string SlotText
		{
			get => _slotText;
			set
			{
				if (!Set(ref _slotText, value ?? "")) return;
				// A new number needs a new look-up and a new OK.
				CheckedSlot = null;
				SlotName = null;
				SlotError = null;
				SlotConfirmed = false;
			}
		}

		public int? CheckedSlot
		{
			get => _checkedSlot;
			private set
			{
				if (!Set(ref _checkedSlot, value)) return;
				SlotChanged();
			}
		}

		public string? SlotName
		{
			get => _slotName;
			private set
			{
				if (!Set(ref _slotName, value)) return;
				SlotChanged();
			}
		}

		public bool SlotChecked => _checkedSlot is not null;
		public bool SlotIsEmpty => _checkedSlot is not null && _slotName is null;

		public string? SlotSummary => _checkedSlot is not int slot ? null : _slotName is null
			? $"Preset {slot} is empty. The quick check can use it; the other checks need a preset that holds a sound."
			: $"Preset {slot} holds “{_slotName}”.";

		public string? SlotError
		{
			get => _slotError;
			private set => Set(ref _slotError, value);
		}

		public bool SlotConfirmed
		{
			get => _slotConfirmed;
			set => Set(ref _slotConfirmed, value);
		}

		// Page 4: level. The edit-buffer and store checks need firmware we have checks for and a slot holding a preset.
		public bool CanUseFull => _identity?.Scripted == true && _checkedSlot is not null && _slotName is not null;

		public CheckLevel Level
		{
			get => _level;
			set
			{
				if (!Set(ref _level, value)) return;
				Changed(nameof(IsQuick));
				Changed(nameof(IsFull));
				Changed(nameof(IsStoreCheck));
				if (value != CheckLevel.StoreCheck) StoreAcknowledged = false;
			}
		}

		public bool IsQuick
		{
			get => _level == CheckLevel.Quick;
			set
			{
				if (value) Level = CheckLevel.Quick;
			}
		}

		public bool IsFull
		{
			get => _level == CheckLevel.Full;
			set
			{
				if (value) Level = CheckLevel.Full;
			}
		}

		public bool IsStoreCheck
		{
			get => _level == CheckLevel.StoreCheck;
			set
			{
				if (value) Level = CheckLevel.StoreCheck;
			}
		}

		public bool StoreAcknowledged
		{
			get => _storeAcknowledged;
			set => Set(ref _storeAcknowledged, value);
		}

		public string StoreWarning => _checkedSlot is int slot
			? $"This one saves a test preset over preset {slot}, then puts your preset back and checks it. Your preset is backed up to this PC first."
			: "This one saves a test preset over your chosen preset, then puts yours back and checks it.";

		// Page 5: run.
		public double ProgressPercent
		{
			get => _progressPercent;
			private set => Set(ref _progressPercent, value);
		}

		public string CurrentStep
		{
			get => _currentStep;
			private set => Set(ref _currentStep, value);
		}

		public string TimeLeft
		{
			get => _timeLeft;
			private set => Set(ref _timeLeft, value);
		}

		public bool IsCancelling
		{
			get => _cancelling;
			private set => Set(ref _cancelling, value);
		}

		// Page 6: done.
		public ProbeRun? Run
		{
			get => _run;
			private set
			{
				if (!Set(ref _run, value)) return;
				Changed(nameof(OutcomeText));
				Changed(nameof(LeftAsFoundText));
				Changed(nameof(LeftAsFound));
			}
		}

		public bool LeftAsFound => _run?.Restore?.LeftAsFound == true;

		public string? OutcomeText => _run?.Outcome switch
		{
			null => null,
			ProbeOutcome.Completed => "All done. Every check ran.",
			ProbeOutcome.CompletedWithFailures => "Finished. A few checks didn't get an answer; the report says which, and that helps us too.",
			ProbeOutcome.Cancelled => "You cancelled the run.",
			ProbeOutcome.Stopped => $"Stopped early to be safe: {_run.StopReason}",
			_ => $"Something went wrong: {_run.StopReason}"
		};

		public string? LeftAsFoundText => _run is null ? null : LeftAsFound
			? "Yes: your device was left exactly as we found it, and we checked."
			: _report?.Backups is string backups
				? $"No: we couldn't confirm your device is exactly as we found it. Backups of your preset and your current sound are in {backups}."
				: "No: we couldn't confirm your device is exactly as we found it. The report says what was checked.";

		public ProbeReportFiles? Report
		{
			get => _report;
			private set
			{
				if (!Set(ref _report, value)) return;
				Changed(nameof(ReportPath));
				Changed(nameof(HasReport));
				Changed(nameof(LeftAsFoundText));
			}
		}

		public bool HasReport => _report is not null;
		public string? ReportPath => _report?.Zip;

		public string? ReportError
		{
			get => _reportError;
			private set => Set(ref _reportError, value);
		}

		// Typed once and saved encrypted for this Windows user (UploadCodeStore); the box is cleared once it's saved.
		public string UploadCode
		{
			get => _uploadCode;
			set => Set(ref _uploadCode, value ?? "");
		}

		public bool HasSavedCode
		{
			get => _hasSavedCode;
			private set => Set(ref _hasSavedCode, value);
		}

		public string? CodeStatus
		{
			get => _codeStatus;
			private set => Set(ref _codeStatus, value);
		}

		// What an upload would send, shown for the user's yes before anything leaves the PC.
		public ReportUploadPreview? UploadPreview
		{
			get => _uploadPreview;
			private set
			{
				if (!Set(ref _uploadPreview, value)) return;
				Changed(nameof(UploadPreviewText));
				Changed(nameof(IsConfirmingUpload));
			}
		}

		public bool IsConfirmingUpload => _uploadPreview is not null;
		public string? UploadPreviewText => _uploadPreview?.Describe().TrimEnd();

		public string? UploadStatus
		{
			get => _uploadStatus;
			private set => Set(ref _uploadStatus, value);
		}

		protected override void OnChanged(string? name)
		{
			// Null during construction, before the commands exist.
			if (_commands is null) return;
			foreach (Command command in _commands)
				command.Refresh();
		}

		private void SlotChanged()
		{
			Changed(nameof(SlotSummary));
			Changed(nameof(SlotChecked));
			Changed(nameof(SlotIsEmpty));
			Changed(nameof(CanUseFull));
			Changed(nameof(StoreWarning));
		}

		private bool CanGoNext() => !_busy && _page switch
		{
			WizardPage.Welcome => _editorsClosed && _disclaimerAccepted,
			WizardPage.Connect => _identity is not null,
			WizardPage.SafeSlot => _checkedSlot is not null && _slotConfirmed,
			WizardPage.Level => _level == CheckLevel.Quick || (CanUseFull && (_level == CheckLevel.Full || _storeAcknowledged)),
			_ => false
		};

		private Task NextAsync()
		{
			if (_page == WizardPage.Level)
			{
				RunTask = RunAsync();
				return RunTask;
			}
			if (_page == WizardPage.SafeSlot && !CanUseFull)
				Level = CheckLevel.Quick;
			Page = _page + 1;
			return Task.CompletedTask;
		}

		private void Back() => Page = _page - 1;

		private async Task ConnectAsync()
		{
			IsBusy = true;
			ConnectError = null;
			Identity = null;
			try
			{
				Identity = await _probe.ConnectAsync(CancellationToken.None);
			}
			catch (Exception exception)
			{
				ConnectError = Plain(exception);
			}
			finally
			{
				IsBusy = false;
			}
		}

		private async Task CheckSlotAsync()
		{
			SlotError = null;
			CheckedSlot = null;
			SlotName = null;
			SlotConfirmed = false;
			if (!int.TryParse(_slotText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int slot))
			{
				SlotError = "Please type a preset number, for example 503.";
				return;
			}
			if (ProbeEngine.SafeSlotProblem(_identity!.Device, slot, _identity.Presets) is not null)
			{
				SlotError = $"That isn't a preset number we can use. {SlotHint}";
				return;
			}
			IsBusy = true;
			try
			{
				string? name = await _probe.ReadPresetNameAsync(_identity, slot, CancellationToken.None);
				CheckedSlot = slot;
				SlotName = name;
			}
			catch (Exception exception)
			{
				SlotError = $"We couldn't read that preset. {Plain(exception)}";
			}
			finally
			{
				IsBusy = false;
			}
		}

		private async Task RunAsync()
		{
			_cancel = new CancellationTokenSource();
			Page = WizardPage.Run;
			_runStarted = _now();
			ProgressPercent = 0;
			CurrentStep = "Getting started";
			TimeLeft = Estimate(_level);
			ProbeRun run;
			try
			{
				ProbeOptions options = new(_checkedSlot!.Value, (int)_level) { AllowStore = _level == CheckLevel.StoreCheck, Progress = this };
				run = await _probe.RunAsync(options, _cancel.Token);
			}
			finally
			{
				_cancel.Dispose();
				_cancel = null;
				IsCancelling = false;
			}
			try
			{
				Report = _probe.WriteReport(run);
			}
			catch (Exception exception)
			{
				ReportError = $"The report couldn't be saved: {exception.Message}";
			}
			ProgressPercent = 100;
			Run = run;
			Page = WizardPage.Done;
		}

		private void Cancel()
		{
			IsCancelling = true;
			CurrentStep = "Cancelling: putting everything back first";
			_cancel?.Cancel();
		}

		private void SaveCode()
		{
			string code = _uploadCode.Trim();
			if (!HttpReportUploader.IsWellFormedCode(code))
			{
				CodeStatus = "That isn't an upload code. Codes start with ftu_ and come from your FracTone account page.";
				return;
			}
			try
			{
				_codes.Save(code);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
			{
				CodeStatus = $"The code couldn't be saved: {exception.Message}";
				return;
			}
			UploadCode = "";
			HasSavedCode = true;
			CodeStatus = "Upload code saved for this Windows user. You only need to do this once.";
		}

		private void ReviewUpload()
		{
			UploadStatus = null;
			try
			{
				UploadPreview = ReportUploadPreview.Read(_report!.Zip);
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
			{
				UploadStatus = $"The report can't be read, so it wasn't sent: {exception.Message}";
			}
		}

		private void DontUpload()
		{
			UploadPreview = null;
			UploadStatus = "Not sent. Your report is still saved, so you can send it later.";
		}

		private async Task UploadAsync()
		{
			string zip = _uploadPreview!.ZipPath;
			UploadPreview = null;
			string? code = _codes.Load();
			if (code is null)
			{
				HasSavedCode = false;
				UploadStatus = NeedsNewCode("Your saved upload code can't be read on this PC.");
				return;
			}
			IsBusy = true;
			UploadStatus = "Uploading...";
			try
			{
				ReportUploadResult result = await _uploader.UploadAsync(zip, code, CancellationToken.None);
				UploadStatus = result switch
				{
					{ Accepted: true } => result.Message,
					{ CodeProblem: true } => NeedsNewCode("The site didn't accept your upload code: it may have been revoked or replaced."),
					_ => $"{result.Message} Your report is still saved, so you can try again later."
				};
				// A refused code stays saved until a new one replaces it, but the box comes back so it can be pasted.
				if (result.CodeProblem) HasSavedCode = false;
			}
			catch (Exception exception)
			{
				UploadStatus = $"The upload didn't go through: {exception.Message} Your report is still saved, so you can try again later.";
			}
			finally
			{
				IsBusy = false;
			}
		}

		// The uploader's own wording names the command line; the window says where to paste instead.
		private static string NeedsNewCode(string reason) =>
			$"{reason} Make a new one on your FracTone account page, paste it above and save it, then upload again. Your report is still saved.";

		// Engine progress; callbacks may come off the UI thread, so they're dispatched.
		void IProgress<ProbeProgress>.Report(ProbeProgress value) => _dispatch(() =>
		{
			if (_cancelling) return;
			if (value.Status is null)
				CurrentStep = StepName(value.Kind);
			if (value.Total <= 0) return;
			ProgressPercent = Math.Round(100.0 * value.Completed / value.Total, 1);
			if (value.Completed >= 2 && value.Completed < value.Total)
			{
				TimeSpan elapsed = _now() - _runStarted;
				TimeSpan left = elapsed / value.Completed * (value.Total - value.Completed);
				TimeLeft = left < TimeSpan.FromMinutes(1) ? "Less than a minute left" : $"About {Math.Ceiling(left.TotalMinutes):0} minutes left";
			}
		});

		// The plan's rough times, until enough steps have run to measure.
		internal static string Estimate(CheckLevel level) => level switch
		{
			CheckLevel.Quick => "Usually about 5 minutes",
			CheckLevel.Full => "Usually 15 to 30 minutes",
			_ => "Usually 20 to 35 minutes"
		};

		internal static string StepName(string kind) => kind switch
		{
			"identify" => "Saying hello to your device",
			"currentPresetAndScene" => "Noting which preset and scene are showing",
			"readEditBuffer" => "Backing up your current sound",
			"readSafeSlot" => "Backing up your chosen preset",
			"unsavedEditsCheck" => "Checking for unsaved changes",
			"statusDump" => "Reading the list of blocks",
			"decodeLayout" => "Reading how the preset is laid out",
			"switchToSafeSlot" => "Switching to your chosen preset",
			"loadTemplate" => "Trying a test preset (not saved)",
			"voicingCheck" => "Checking amp and effect models",
			"storeCheck" => "Testing saving to your chosen preset",
			"restore" => "Putting everything back and checking it",
			_ => "Working"
		};

		// The engine's stop messages are already written for people; anything else gets the usual cause.
		private static string Plain(Exception exception) => exception switch
		{
			ProbeStopException or InvalidOperationException or PlatformNotSupportedException => exception.Message,
			_ => $"{exception.Message} Make sure the editor apps are closed and the USB cable is connected, then try again."
		};
	}
}
