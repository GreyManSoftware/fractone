using Fractal.Companion.Protocol;

namespace FracTone.Probe
{
	public sealed record PlannedFrame(string Step, string Frame, string Note);

	// The dry run: every frame a run would send, in order, without opening a port. Frames that depend on what the device
	// answers (going back to the preset found, a resend after a dropped load) are listed as notes. Each frame goes through
	// the same allow-list as a real run.
	public static class ProbePlanner
	{
		public static IReadOnlyList<PlannedFrame> Plan(Gen3Device device, string firmware, int slot, int level, bool allowStore)
		{
			ArgumentNullException.ThrowIfNull(device);
			ProbeScript? scripted = ProbeScripts.Find(device, firmware);
			ProbeScript script = scripted ?? ProbeScripts.ReadOnly(device, firmware);
			int levelRun = scripted is null ? 1 : level;
			ProbeCommandPolicy policy = new(device, slot, null, allowStore && levelRun == 3);
			List<PlannedFrame> plan = [];
			void Add(string step, byte[] frame, string note)
			{
				if (step == "identify") ProbeCommandPolicy.CheckIdentity(frame); else policy.Check(frame);
				plan.Add(new PlannedFrame(step, ProbeFrames.Hex(frame), note));
			}
			void Note(string step, string note) => plan.Add(new PlannedFrame(step, "", note));

			Add("identify", ProbeFrames.Firmware(device.Model), $"firmware request ({device.Name}; the other models are asked if it stays silent)");
			if (device == Gen3Device.AxeFx3)
			{
				Add("identify-variant", ProbeFrames.StoredPresetName(device, ProbeEngine.MkIICheckPreset),
					"preset 1023's name: a name means Mk II or Turbo (1,024 presets); error 0E or silence keeps the run to 0-511");
				if (slot >= 512)
					Note("identify-variant", $"the safe slot {slot} needs a Mk II or Turbo answer; a Mk I or no answer stops the run here");
			}
			if (scripted is null)
				Note("identify", $"no script for {device.Name} {firmware}: read-only steps only, labelled unverified");
			Add("where", ProbeFrames.CurrentPreset(device), "preset showing");
			Add("where", ProbeFrames.CurrentScene(device), "scene playing");
			Add("backup-edit-buffer", ProbeFrames.ReadPreset(device, ProbeFrames.EditBuffer), "read the edit buffer (kept verbatim)");
			Add("backup-safe-slot", ProbeFrames.ReadPreset(device, slot), $"read the safe slot {slot} (kept verbatim)");
			Note("unsaved-edits", "read the stored copy of the preset showing (03 <preset>) and compare; stop at level 2+ if it differs");
			bool onSafeSlot = false;
			foreach (ProbeStep step in script.Steps.Where(step => step.Level <= levelRun))
				switch (step.Kind)
				{
					case ProbeStepKind.StatusDump:
						Add(step.Id, ProbeFrames.StatusDump(device), "status dump");
						break;
					case ProbeStepKind.DecodeLayout:
						Note(step.Id, $"decode the {step.Source} read (nothing sent)");
						break;
					case ProbeStepKind.SwitchToSafeSlot:
						Switch(step.Id, slot);
						onSafeSlot = true;
						break;
					case ProbeStepKind.LoadTemplate:
					case ProbeStepKind.StoreCheck:
						if (!onSafeSlot) Note(step.Id, "refused: needs the switch to the safe slot first");
						Add(step.Id, ProbeFrames.CurrentPreset(device), "confirm the device is still on the safe slot");
						IReadOnlyList<byte[]> template = ProbeFrames.ForEditBuffer(script.LoadTemplate());
						foreach (byte[] frame in template)
							Add(step.Id, frame, $"template {script.Template} into the edit buffer, frame 0x{frame[5]:X2} ({frame.Length} bytes)");
						Add(step.Id, ProbeFrames.ReadPreset(device, ProbeFrames.EditBuffer), "read back and compare (sent once more if dropped)");
						if (step.Kind == ProbeStepKind.StoreCheck)
						{
							Add(step.Id, ProbeFrames.StorePreset(device, slot), $"store to the safe slot {slot} (level 3 only)");
							Add(step.Id, ProbeFrames.ReadPreset(device, slot), "read the slot back");
							Note(step.Id, "resend the safe slot's backup frames (header 7F 7F, data verbatim), read back, store, read the slot back");
						}
						break;
					case ProbeStepKind.VoicingCheck:
						if (step.Types is not { Count: > 0 } || step.EffectId is not int effect || step.ParameterId is not int parameter)
						{
							Note(step.Id, "voicing spot check: no types listed yet, nothing sent");
							break;
						}
						BeginCheck(step);
						foreach (ProbeInsert insert in step.Inserts?.Where(insert => insert.EffectId == effect) ?? [])
							Insert(step.Id, insert, "only if the buffer lacks it");
						foreach (ProbeVoicing type in step.Types)
						{
							Add(step.Id, ProbeFrames.SelectType(device, effect, parameter, type.Ordinal), $"select {step.Block} type {type.Ordinal}{(type.Name is null ? "" : $" ({type.Name})")} (sent once more if it didn't take)");
							Add(step.Id, ProbeFrames.BulkRead(device, effect), "read the block's words back (1F)");
						}
						break;
					case ProbeStepKind.FreshInserts:
						BeginCheck(step);
						foreach (ProbeInsert insert in step.Inserts ?? [])
							Insert(step.Id, insert, "one at a time; first since a power cycle: unknown");
						break;
					case ProbeStepKind.CableCheck:
						BeginCheck(step);
						foreach (ProbeInsert insert in step.Inserts ?? [])
							Insert(step.Id, insert, "a block to cable");
						foreach (ProbeRoute route in step.Routes ?? [])
						{
							Add(step.Id, ProbeFrames.Route(device, route.FromRow, route.FromColumn, route.ToRow), $"cable {route.Id}");
							Add(step.Id, ProbeFrames.ReadPreset(device, ProbeFrames.EditBuffer), "read the grid's route masks back");
						}
						break;
				}
			if (levelRun >= 2)
			{
				Switch("restore", slot);
				Note("restore", "then 01 27 back to the preset found (if not the safe slot), read the edit buffer and compare with its backup");
				Add("restore", ProbeFrames.CurrentScene(device), "check the scene; 0C <scene> selects the scene found if it differs");
			}
			else
			{
				Add("restore", ProbeFrames.CurrentPreset(device), "nothing changed: confirm the preset showing");
				Add("restore", ProbeFrames.ReadPreset(device, ProbeFrames.EditBuffer), "and that the edit buffer still equals its backup");
			}
			return plan;

			void BeginCheck(ProbeStep step)
			{
				if (!onSafeSlot) Note(step.Id, "refused: needs the switch to the safe slot first");
				Add(step.Id, ProbeFrames.CurrentPreset(device), "confirm the device is still on the safe slot");
				if (step.Template is not string which) return;
				foreach (byte[] frame in ProbeFrames.ForEditBuffer(script.LoadTemplate(which)))
					Add(step.Id, frame, $"{which} template into the edit buffer, frame 0x{frame[5]:X2} ({frame.Length} bytes)");
				Add(step.Id, ProbeFrames.ReadPreset(device, ProbeFrames.EditBuffer), "read back and compare (sent once more if dropped)");
			}

			void Insert(string step, ProbeInsert insert, string note)
			{
				IReadOnlyList<byte[]> frames = ProbeFrames.InsertBlock(device, insert.EffectId, insert.Row, insert.Column);
				Add(step, frames[0], $"select r{insert.Row}c{insert.Column}");
				Add(step, frames[1], $"insert {insert.Block} (effect {insert.EffectId}), {note}");
				Add(step, ProbeFrames.ReadPreset(device, ProbeFrames.EditBuffer), "read the buffer back: the block in its cell, its record");
			}

			void Switch(string step, int target)
			{
				Add(step, ProbeFrames.SwitchPreset(device, target), $"01 27 switch to {target} (never Program Change)");
				Add(step, ProbeFrames.CurrentPreset(device), $"confirm the device is on {target}");
				Add(step, ProbeFrames.ReadPreset(device, ProbeFrames.EditBuffer), "read the buffer and compare with the slot's backup");
			}
		}
	}
}
