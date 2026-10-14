# Release checklist

CI cannot reach an FM9. Run these steps on a Windows PC with the FM9 connected and FM9-Edit installed, before tagging a
release.

## Before you start

- Use only scratch slots (for example 500-505), and back them up first with `backup`.
- Close FM9-Edit or disconnect it from the FM9 if MIDI calls fail.
- Run `dnx FracTone setup --careful --writable 500-505`.

## Tests

1. `dotnet test Fractone.slnx`. Every test must run; none may be skipped for a missing dictionary.
2. `./scripts/check-public.ps1` must pass.

## Hardware

1. **Status.** `status` shows the firmware, preset and scene.
2. **Reads.** `read_preset` reads a scratch slot, and `backup` reads 0-511 (about 2-3 minutes) with no
   failures.
3. **Index.** `index_build` from the backup, then `search` for a model you know you use.
4. **Audition.** `audition` loads a preset with a non-standard layout into a scratch slot. It plays, and nothing is
   stored.
5. **Store.**
   - `store_preset` into a scratch slot returns a confirmation code. The second call stores, with `verified: true`
     and a backup.
   - A slot outside the writable range is refused.
6. **Model change.** Change an amp model with `set_channel`, then store it. The store must verify.
7. **Restore.** Store the backup back. A fresh read must match the backup byte for byte.
8. **Live control.**
   - On a scratch slot, `set_parameters` changes Gain on the channel playing and reads it back.
   - Naming another channel switches to it. A model by name loads its voicing: compare with a model chosen on the FM9.
   - `set_block` flips a bypass, and `select_scene` keeps live edits.
   - `select_preset` is refused over the unsaved edits while the safeguard is on.
   - `store_edit_buffer` stores them, and a fresh read shows them.
9. **Favourites.**
   - Save a block with `library_save`.
   - Load it in FM9-Edit onto the same block of a scratch preset.
   - Read the edit buffer: the block's channels must match the favourite.
10. **Recording.** `record` from the FM9 and from the system output each produce a WAV with sound in it.
11. **Clean up.** Restore the scratch slots and select the preset that was showing before.
