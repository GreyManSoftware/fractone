using System.IO.Compression;
using Fractal.Companion.Protocol;
using FracTone.Probe.Gui.Services;
using FracTone.Probe.Gui.ViewModels;

namespace FracTone.Probe.Gui.Tests
{
	public sealed class WizardViewModelTests : IDisposable
	{
		private const string Code = "ftu_TestCode0123456789abcdefABCDEF";
		private static readonly ProbeIdentity Fm9 = new(Gen3Device.Fm9, "12.00", Scripted: true, Presets: 512);
		// Holds the saved upload code and any report zip a test writes; the real user's code is never touched.
		private readonly string _folder = Path.Combine(Path.GetTempPath(), "fractone-gui-tests-" + Guid.NewGuid().ToString("N"));

		public WizardViewModelTests() => Directory.CreateDirectory(_folder);

		public void Dispose()
		{
			if (Directory.Exists(_folder))
				Directory.Delete(_folder, recursive: true);
		}

		[Fact]
		public async Task TheWizardWalksFromBeforeYouStartToDoneInOrder()
		{
			FakeProbe probe = new();
			WizardViewModel wizard = New(probe);

			Assert.Equal(WizardPage.Welcome, wizard.Page);
			await ToLevel(wizard);
			Assert.Equal(WizardPage.Level, wizard.Page);
			wizard.IsFull = true;
			Task run = wizard.NextCommand.ExecuteAsync();
			Assert.Equal(WizardPage.Run, wizard.Page);
			probe.Finish(Run(ProbeOutcome.Completed, leftAsFound: true));
			await run;

			Assert.Equal(WizardPage.Done, wizard.Page);
			Assert.Equal((503, 2, false), (probe.Options!.Slot, probe.Options.Level, probe.Options.AllowStore));
			Assert.Equal("All done. Every check ran.", wizard.OutcomeText);
			Assert.StartsWith("Yes", wizard.LeftAsFoundText, StringComparison.Ordinal);
			Assert.Equal(probe.Zip, wizard.ReportPath);
		}

		[Theory]
		[InlineData(false, false)]
		[InlineData(true, false)]
		[InlineData(false, true)]
		public async Task NextStaysOffUntilTheEditorsAreClosedAndTheDisclaimerIsAccepted(bool editorsClosed, bool disclaimerAccepted)
		{
			WizardViewModel wizard = New(new FakeProbe());
			Assert.Contains("at your own risk", wizard.DisclaimerText, StringComparison.Ordinal);
			wizard.EditorsClosed = editorsClosed;
			wizard.DisclaimerAccepted = disclaimerAccepted;

			Assert.False(wizard.NextCommand.CanExecute());
			await wizard.NextCommand.ExecuteAsync();
			Assert.Equal(WizardPage.Welcome, wizard.Page);
			wizard.EditorsClosed = wizard.DisclaimerAccepted = true;
			Assert.True(wizard.NextCommand.CanExecute());
		}

		[Fact]
		public async Task AFailedConnectionStaysOnTheConnectPageWithTheReason()
		{
			FakeProbe probe = new() { ConnectFailure = new ProbeStopException("No Fractal device answered.") };
			WizardViewModel wizard = New(probe);
			wizard.EditorsClosed = wizard.DisclaimerAccepted = true;
			await wizard.NextCommand.ExecuteAsync();

			await wizard.ConnectCommand.ExecuteAsync();

			Assert.Equal("No Fractal device answered.", wizard.ConnectError);
			Assert.False(wizard.NextCommand.CanExecute());
		}

		[Theory]
		[InlineData("abc", "Please type a preset number")]
		[InlineData("-3", "Please type a preset number")]
		[InlineData("512", "That isn't a preset number we can use")]
		public async Task ASlotThatIsNotOneOfTheDevicesPresetsIsRefusedBeforeAnythingIsRead(string text, string error)
		{
			FakeProbe probe = new();
			WizardViewModel wizard = await ToSafeSlot(probe);

			wizard.SlotText = text;
			await wizard.CheckSlotCommand.ExecuteAsync();

			Assert.StartsWith(error, wizard.SlotError, StringComparison.Ordinal);
			Assert.Empty(probe.SlotsRead);
			Assert.False(wizard.NextCommand.CanExecute());
		}

		[Fact]
		public async Task AnAxeFx3MkIIOffersItsPresetsAbove511()
		{
			FakeProbe probe = new() { Identity = new ProbeIdentity(Gen3Device.AxeFx3, "32.06", Scripted: true, Presets: 1024, ProbeDeviceVariant.MkIIOrTurbo) };
			WizardViewModel wizard = await ToSafeSlot(probe);

			wizard.SlotText = "900";
			await wizard.CheckSlotCommand.ExecuteAsync();

			Assert.Equal("Type a preset number from 0 to 1023.", wizard.SlotHint);
			Assert.Equal([900], probe.SlotsRead);
		}

		[Fact]
		public async Task TheSlotsNameIsShownAndNextNeedsTheUsersOkWhichANewNumberClears()
		{
			FakeProbe probe = new();
			WizardViewModel wizard = await ToSafeSlot(probe);

			wizard.SlotText = "503";
			await wizard.CheckSlotCommand.ExecuteAsync();

			Assert.Equal([503], probe.SlotsRead);
			Assert.Contains("Spare Slot", wizard.SlotSummary, StringComparison.Ordinal);
			Assert.False(wizard.NextCommand.CanExecute());
			wizard.SlotConfirmed = true;
			Assert.True(wizard.NextCommand.CanExecute());
			wizard.SlotText = "504";
			Assert.Equal((false, (int?)null), (wizard.SlotConfirmed, wizard.CheckedSlot));
			Assert.False(wizard.NextCommand.CanExecute());
		}

		[Fact]
		public async Task AnEmptySlotOffersOnlyTheQuickCheck()
		{
			FakeProbe probe = new() { SlotName = null };
			WizardViewModel wizard = await ToSafeSlot(probe);
			wizard.SlotText = "503";
			await wizard.CheckSlotCommand.ExecuteAsync();
			wizard.SlotConfirmed = true;
			await wizard.NextCommand.ExecuteAsync();

			Assert.True(wizard.SlotIsEmpty);
			Assert.False(wizard.CanUseFull);
			wizard.IsFull = true;
			Assert.False(wizard.NextCommand.CanExecute());
			wizard.IsQuick = true;
			Assert.True(wizard.NextCommand.CanExecute());
		}

		[Fact]
		public async Task FirmwareWithoutChecksOffersOnlyTheQuickCheck()
		{
			FakeProbe probe = new() { Identity = Fm9 with { Firmware = "13.00", Scripted = false } };
			WizardViewModel wizard = New(probe);

			await ToLevel(wizard);

			Assert.False(wizard.CanUseFull);
			Assert.Contains("only the quick", wizard.ScriptedSummary, StringComparison.Ordinal);
		}

		[Fact]
		public async Task TheStoreCheckStartsOnlyAfterItsWarningIsAcknowledged()
		{
			FakeProbe probe = new();
			WizardViewModel wizard = New(probe);
			await ToLevel(wizard);

			wizard.IsStoreCheck = true;
			Assert.False(wizard.NextCommand.CanExecute());
			wizard.StoreAcknowledged = true;
			Assert.True(wizard.NextCommand.CanExecute());
			Task run = wizard.NextCommand.ExecuteAsync();
			probe.Finish(Run(ProbeOutcome.Completed, leftAsFound: true));
			await run;

			Assert.Equal((3, true), (probe.Options!.Level, probe.Options.AllowStore));
		}

		[Fact]
		public async Task CancelSignalsTheEngineAndTheDonePageSaysWhetherTheDeviceWasLeftAsFound()
		{
			FakeProbe probe = new();
			WizardViewModel wizard = New(probe);
			await ToLevel(wizard);
			Task run = wizard.NextCommand.ExecuteAsync();

			Assert.True(wizard.CancelCommand.CanExecute());
			await wizard.CancelCommand.ExecuteAsync();

			Assert.True(probe.Token.IsCancellationRequested);
			Assert.True(wizard.IsCancelling);
			Assert.False(wizard.CancelCommand.CanExecute());
			// The engine restores before it returns; the wizard waits for that.
			Assert.Equal(WizardPage.Run, wizard.Page);
			probe.Finish(Run(ProbeOutcome.Cancelled, leftAsFound: true));
			await run;
			Assert.Equal(WizardPage.Done, wizard.Page);
			Assert.Equal("You cancelled the run.", wizard.OutcomeText);
			Assert.True(wizard.LeftAsFound);
		}

		[Fact]
		public async Task ADeviceNotVerifiedAsFoundPointsTheUserToTheirBackups()
		{
			FakeProbe probe = new();
			WizardViewModel wizard = New(probe);
			await ToLevel(wizard);
			Task run = wizard.NextCommand.ExecuteAsync();
			probe.Finish(Run(ProbeOutcome.Stopped, leftAsFound: false));
			await run;

			Assert.StartsWith("No", wizard.LeftAsFoundText, StringComparison.Ordinal);
			Assert.Contains(probe.Backups, wizard.LeftAsFoundText, StringComparison.Ordinal);
		}

		[Fact]
		public async Task ProgressShowsTheCurrentStepInPlainWordsAndTheShareDone()
		{
			FakeProbe probe = new();
			WizardViewModel wizard = New(probe);
			await ToLevel(wizard);
			Task run = wizard.NextCommand.ExecuteAsync();

			IProgress<ProbeProgress> progress = probe.Options!.Progress!;
			progress.Report(new ProbeProgress(2, 8, "backup-edit-buffer", "readEditBuffer", null, null));

			Assert.Equal("Backing up your current sound", wizard.CurrentStep);
			Assert.Equal(25, wizard.ProgressPercent);
			Assert.False(string.IsNullOrEmpty(wizard.TimeLeft));
			probe.Finish(Run(ProbeOutcome.Completed, leftAsFound: true));
			await run;
		}

		[Fact]
		public async Task UploadStaysOffUntilAReportExistsAndACodeIsSaved()
		{
			FakeProbe probe = new() { Folder = _folder };
			WizardViewModel wizard = New(probe);
			Assert.False(wizard.HasSavedCode);
			Assert.False(wizard.ReviewUploadCommand.CanExecute());
			Assert.False(wizard.OpenFolderCommand.CanExecute());

			await ToLevel(wizard);
			Task run = wizard.NextCommand.ExecuteAsync();
			probe.Finish(Run(ProbeOutcome.Completed, leftAsFound: true));
			await run;
			Assert.False(wizard.ReviewUploadCommand.CanExecute());
			Assert.False(wizard.SaveCodeCommand.CanExecute());

			wizard.UploadCode = $" {Code} ";
			await wizard.SaveCodeCommand.ExecuteAsync();

			Assert.True(wizard.HasSavedCode);
			Assert.Equal("", wizard.UploadCode);
			Assert.Equal(Code, Store().Load());
			Assert.True(wizard.ReviewUploadCommand.CanExecute());
		}

		[Fact]
		public async Task SomethingThatIsNotAnUploadCodeIsNotSaved()
		{
			WizardViewModel wizard = New(new FakeProbe());

			wizard.UploadCode = "abc_123456789";
			await wizard.SaveCodeCommand.ExecuteAsync();

			Assert.False(wizard.HasSavedCode);
			Assert.Null(Store().Load());
			Assert.StartsWith("That isn't an upload code", wizard.CodeStatus, StringComparison.Ordinal);
		}

		[Fact]
		public void ACodeSavedEarlierIsUsedWithoutAskingAgain()
		{
			Store().Save(Code);

			WizardViewModel wizard = New(new FakeProbe());

			Assert.True(wizard.HasSavedCode);
		}

		[Fact]
		public async Task TheUploadShowsWhatWillBeSentAndSendsNothingUntilTheUserSaysYes()
		{
			Store().Save(Code);
			FakeUploader uploader = new();
			(WizardViewModel wizard, FakeProbe probe) = await ToDone(uploader);

			await wizard.ReviewUploadCommand.ExecuteAsync();

			Assert.True(wizard.IsConfirmingUpload);
			Assert.Contains("run.json", wizard.UploadPreviewText, StringComparison.Ordinal);
			Assert.Contains("Anonymised: yes", wizard.UploadPreviewText, StringComparison.Ordinal);
			Assert.Null(uploader.Sent);
			await wizard.DontUploadCommand.ExecuteAsync();
			Assert.False(wizard.IsConfirmingUpload);
			Assert.Null(uploader.Sent);
			Assert.StartsWith("Not sent", wizard.UploadStatus, StringComparison.Ordinal);

			await wizard.ReviewUploadCommand.ExecuteAsync();
			await wizard.UploadCommand.ExecuteAsync();

			Assert.Equal((Path.GetFullPath(probe.Zip), Code), uploader.Sent);
			Assert.Equal("Uploaded: report r42. Thank you.", wizard.UploadStatus);
			Assert.False(wizard.IsConfirmingUpload);
		}

		[Theory]
		[InlineData("refused", "The site didn't accept your upload code", false)]
		[InlineData("failed", "The site had a problem (HTTP 503). Your report is still saved", true)]
		[InlineData("threw", "The upload didn't go through: Network down. Your report is still saved", true)]
		public async Task AFailedUploadIsExplainedInPlainWordsAndAsksForANewCodeOnlyWhenTheCodeWasRefused(string outcome, string expected, bool codeStillUsable)
		{
			Store().Save(Code);
			FakeUploader uploader = new()
			{
				Result = outcome switch
				{
					"refused" => new ReportUploadResult(false, "run: fractone-probe login --code ftu_...", CodeProblem: true),
					"failed" => new ReportUploadResult(false, "The site had a problem (HTTP 503)."),
					_ => null
				},
				Failure = outcome == "threw" ? new HttpRequestException("Network down.") : null
			};
			(WizardViewModel wizard, _) = await ToDone(uploader);

			await wizard.ReviewUploadCommand.ExecuteAsync();
			await wizard.UploadCommand.ExecuteAsync();

			Assert.StartsWith(expected, wizard.UploadStatus, StringComparison.Ordinal);
			Assert.DoesNotContain("fractone-probe", wizard.UploadStatus, StringComparison.Ordinal);
			Assert.DoesNotContain(Code, wizard.UploadStatus, StringComparison.Ordinal);
			Assert.Equal(codeStillUsable, wizard.HasSavedCode);
			Assert.False(wizard.IsBusy);
		}

		private UploadCodeStore Store() => new(Path.Combine(_folder, "store"));

		private WizardViewModel New(FakeProbe probe, FakeUploader? uploader = null) =>
			new(probe, uploader ?? new FakeUploader(), Store(), new FakeFolders(), now: () => new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));

		private async Task<(WizardViewModel Wizard, FakeProbe Probe)> ToDone(FakeUploader uploader)
		{
			FakeProbe probe = new() { Folder = _folder };
			WizardViewModel wizard = New(probe, uploader);
			await ToLevel(wizard);
			Task run = wizard.NextCommand.ExecuteAsync();
			probe.Finish(Run(ProbeOutcome.Completed, leftAsFound: true));
			await run;
			return (wizard, probe);
		}

		private async Task<WizardViewModel> ToSafeSlot(FakeProbe probe)
		{
			WizardViewModel wizard = New(probe);
			wizard.EditorsClosed = wizard.DisclaimerAccepted = true;
			await wizard.NextCommand.ExecuteAsync();
			await wizard.ConnectCommand.ExecuteAsync();
			await wizard.NextCommand.ExecuteAsync();
			Assert.Equal(WizardPage.SafeSlot, wizard.Page);
			return wizard;
		}

		private static async Task ToLevel(WizardViewModel wizard)
		{
			wizard.EditorsClosed = wizard.DisclaimerAccepted = true;
			await wizard.NextCommand.ExecuteAsync();
			await wizard.ConnectCommand.ExecuteAsync();
			await wizard.NextCommand.ExecuteAsync();
			wizard.SlotText = "503";
			await wizard.CheckSlotCommand.ExecuteAsync();
			wizard.SlotConfirmed = true;
			await wizard.NextCommand.ExecuteAsync();
		}

		private static ProbeRun Run(ProbeOutcome outcome, bool leftAsFound) => new()
		{
			Outcome = outcome,
			StopReason = outcome == ProbeOutcome.Stopped ? "The device stopped answering." : null,
			Restore = new ProbeRestore(true, leftAsFound, leftAsFound, leftAsFound, leftAsFound, null, [])
		};

		private sealed class FakeProbe : IProbeService
		{
			private readonly TaskCompletionSource<ProbeRun> _run = new(TaskCreationOptions.RunContinuationsAsynchronously);

			public ProbeIdentity Identity { get; init; } = Fm9;
			public Exception? ConnectFailure { get; init; }
			public string? SlotName { get; init; } = "Spare Slot";
			public List<int> SlotsRead { get; } = [];
			public ProbeOptions? Options { get; private set; }
			public CancellationToken Token { get; private set; }
			// Set when a test needs the report zip to exist (the upload preview reads it).
			public string? Folder { get; init; }
			public string Zip => Path.Combine(Folder ?? "reports", "fractone-probe-FM9.zip");
			public string Backups => Path.Combine(Folder ?? "reports", "fractone-probe-FM9-backups");

			public Task<ProbeIdentity> ConnectAsync(CancellationToken cancellationToken) =>
				ConnectFailure is null ? Task.FromResult(Identity) : Task.FromException<ProbeIdentity>(ConnectFailure);

			public Task<string?> ReadPresetNameAsync(ProbeIdentity identity, int slot, CancellationToken cancellationToken)
			{
				SlotsRead.Add(slot);
				return Task.FromResult(SlotName);
			}

			public Task<ProbeRun> RunAsync(ProbeOptions options, CancellationToken cancellationToken)
			{
				Options = options;
				Token = cancellationToken;
				return _run.Task;
			}

			public ProbeReportFiles WriteReport(ProbeRun run)
			{
				if (Folder is not null)
				{
					using ZipArchive zip = ZipFile.Open(Zip, ZipArchiveMode.Create);
					using StreamWriter json = new(zip.CreateEntry("run.json").Open());
					json.Write("{\"userPresetsIncluded\":false}");
				}
				return new ProbeReportFiles(Zip, Backups, ["run.json"]);
			}

			public void Finish(ProbeRun run) => _run.SetResult(run);
		}

		private sealed class FakeUploader : IReportUploader
		{
			public (string Zip, string Code)? Sent { get; private set; }
			public ReportUploadResult? Result { get; init; } = new(true, "Uploaded: report r42. Thank you.", "r42");
			public Exception? Failure { get; init; }

			public Task<ReportUploadResult> UploadAsync(string zipPath, string uploadCode, CancellationToken cancellationToken)
			{
				Sent = (zipPath, uploadCode);
				return Failure is null ? Task.FromResult(Result!) : Task.FromException<ReportUploadResult>(Failure);
			}
		}

		private sealed class FakeFolders : IFolderOpener
		{
			public void Open(string folder)
			{
			}
		}
	}
}
