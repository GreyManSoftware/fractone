using System.Text;

namespace Fractal.Companion.Protocol
{
	public enum Fm9SceneChannel : ushort
	{
		A = 0,
		B = 1,
		C = 2,
		D = 3
	}

	public enum Fm9RideSceneBlock
	{
		Input,
		Gate,
		Drive,
		Amp,
		Cab,
		ParametricEq,
		Reverb,
		Output
	}

	public sealed record Fm9RideSceneBlockState(
		Fm9RideSceneBlock Block,
		Fm9SceneChannel Channel,
		bool Bypassed);

	public sealed record Fm9RideScene(
		int Number,
		string Name,
		IReadOnlyList<Fm9RideSceneBlockState> BlockStates);

	public sealed record Fm9PresetBlockSceneState(
		Fm9SceneChannel Channel,
		bool Bypassed);

	public static class Fm9PresetBodySceneAuthor
	{
		private const int SceneCount = 8;
		private const int SceneNameOffset = 4;
		private const int SceneNameLength = 32;
		// Scene 1's channel precedes the 23-word record header; scenes 2-8 use words 0-6.
		private const int SceneChannelFirstHeaderWord = -1;
		private const int SceneBypassFirstHeaderWord = 7;

		private static readonly IReadOnlyDictionary<Fm9RideSceneBlock, (ushort Columns, ushort Rows, int EffectId)> RideRecords =
			new Dictionary<Fm9RideSceneBlock, (ushort Columns, ushort Rows, int EffectId)>
			{
				[Fm9RideSceneBlock.Input] = (10, 4, 37),
				[Fm9RideSceneBlock.Gate] = (19, 4, 146),
				[Fm9RideSceneBlock.Drive] = (44, 4, 118),
				[Fm9RideSceneBlock.Amp] = (147, 4, 58),
				[Fm9RideSceneBlock.Cab] = (106, 4, 62),
				[Fm9RideSceneBlock.ParametricEq] = (33, 4, 54),
				[Fm9RideSceneBlock.Reverb] = (73, 4, 66),
				[Fm9RideSceneBlock.Output] = (26, 4, 42)
			};

		public static Fm9PresetBlockSceneState GetSceneState(
			Fm9PresetBody source,
			Fm9PresetBlockRecord record,
			int sceneNumber)
		{
			ArgumentNullException.ThrowIfNull(source);
			ArgumentNullException.ThrowIfNull(record);
			ValidateSceneNumber(sceneNumber);
			ValidateRecord(source, record);

			ushort rawChannel = ReadUInt16(
				source.Bytes.Span,
				GetSceneChannelOffset(record, sceneNumber));

			if (rawChannel > (ushort)Fm9SceneChannel.D)
			{
				throw new InvalidDataException(
					$"The FM9 record has an unsupported scene channel value {rawChannel}.");
			}

			ushort rawBypass = ReadUInt16(
				source.Bytes.Span,
				GetSceneBypassOffset(record, sceneNumber));

			if (rawBypass > 1)
			{
				throw new InvalidDataException(
					$"The FM9 record has an unsupported scene bypass value {rawBypass}.");
			}

			return new Fm9PresetBlockSceneState((Fm9SceneChannel)rawChannel, rawBypass == 1);
		}

		public static byte[] WithRideScenes(
			Fm9PresetBody source,
			IReadOnlyList<Fm9RideScene> scenes)
		{
			Dictionary<Fm9RideSceneBlock, Fm9PresetBlockRecord> records = ValidateAndResolveRideScenes(source, scenes);
			byte[] bytes = source.Bytes.ToArray();
			foreach (Fm9RideScene scene in scenes)
			{
				foreach (Fm9RideSceneBlockState state in scene.BlockStates)
				{
					Fm9PresetBlockRecord record = records[state.Block];
					WriteUInt16(bytes, GetSceneChannelOffset(record, scene.Number), (ushort)state.Channel);
					WriteUInt16(bytes, GetSceneBypassOffset(record, scene.Number), state.Bypassed ? (ushort)1 : (ushort)0);
				}
				WriteSceneName(bytes, scene.Number, scene.Name);
			}
			// Scenes the proposal does not author would otherwise keep the template's factory states (other channels,
			// bypassed blocks), so stepping onto one plays something nobody designed. They play scene 1 instead.
			Fm9RideScene? first = scenes.FirstOrDefault(scene => scene.Number == 1);
			if (first is not null)
			{
				foreach (int unauthored in Enumerable.Range(1, SceneCount).Where(number => scenes.All(scene => scene.Number != number)))
				{
					foreach (Fm9RideSceneBlockState state in first.BlockStates)
					{
						Fm9PresetBlockRecord record = records[state.Block];
						WriteUInt16(bytes, GetSceneChannelOffset(record, unauthored), (ushort)state.Channel);
						WriteUInt16(bytes, GetSceneBypassOffset(record, unauthored), state.Bypassed ? (ushort)1 : (ushort)0);
					}
				}
			}
			return bytes;
		}

		// Sets one block's bypass in one scene, addressed by the block's own record, so it works on any preset layout
		// (including user-added blocks the Ride template does not know); every other block and scene is untouched.
		public static byte[] WithSceneBypass(Fm9PresetBody source, Fm9PresetBlockRecord record, int sceneNumber, bool bypassed)
		{
			ArgumentNullException.ThrowIfNull(source);
			ArgumentNullException.ThrowIfNull(record);
			ValidateSceneNumber(sceneNumber);
			ValidateRecord(source, record);
			byte[] bytes = source.Bytes.ToArray();
			WriteUInt16(bytes, GetSceneBypassOffset(record, sceneNumber), bypassed ? (ushort)1 : (ushort)0);
			return bytes;
		}

		public static byte[] WithSceneChannel(Fm9PresetBody source, Fm9PresetBlockRecord record, int sceneNumber, Fm9SceneChannel channel)
		{
			ArgumentNullException.ThrowIfNull(source);
			ArgumentNullException.ThrowIfNull(record);
			ValidateSceneNumber(sceneNumber);
			ValidateRecord(source, record);
			if (!Enum.IsDefined(channel))
				throw new ArgumentOutOfRangeException(nameof(channel));
			byte[] bytes = source.Bytes.ToArray();
			WriteUInt16(bytes, GetSceneChannelOffset(record, sceneNumber), (ushort)channel);
			return bytes;
		}

		public static byte[] WithSceneName(Fm9PresetBody source, int sceneNumber, string name)
		{
			ArgumentNullException.ThrowIfNull(source);
			ValidateSceneNumber(sceneNumber);
			ValidateSceneName(name);
			byte[] bytes = source.Bytes.ToArray();
			WriteSceneName(bytes, sceneNumber, name);
			return bytes;
		}

		// Moves one block to channel A in every scene, leaving its bypass states and all other blocks untouched.
		public static byte[] WithBlockOnChannelAInEveryScene(
			Fm9PresetBody source,
			Fm9PresetBlockRecord record)
		{
			ArgumentNullException.ThrowIfNull(source);
			ArgumentNullException.ThrowIfNull(record);
			ValidateRecord(source, record);
			byte[] bytes = source.Bytes.ToArray();
			for (int sceneNumber = 1; sceneNumber <= SceneCount; sceneNumber++)
				WriteUInt16(bytes, GetSceneChannelOffset(record, sceneNumber), (ushort)Fm9SceneChannel.A);
			return bytes;
		}

		public static void ValidateRideScenes(Fm9PresetBody source, IReadOnlyList<Fm9RideScene> scenes)
		{
			_ = ValidateAndResolveRideScenes(source, scenes);
		}

		private static Dictionary<Fm9RideSceneBlock, Fm9PresetBlockRecord> ValidateAndResolveRideScenes(
			Fm9PresetBody source, IReadOnlyList<Fm9RideScene> scenes)
		{
			ArgumentNullException.ThrowIfNull(source);
			ArgumentNullException.ThrowIfNull(scenes);

			if (scenes.Count == 0)
			{
				throw new ArgumentException("At least one FM9 scene is required.", nameof(scenes));
			}

			Dictionary<Fm9RideSceneBlock, Fm9PresetBlockRecord> records = ResolveRideRecords(source);
			HashSet<int> sceneNumbers = [];

			foreach (Fm9RideScene scene in scenes)
			{
				ArgumentNullException.ThrowIfNull(scene);
				ArgumentNullException.ThrowIfNull(scene.BlockStates);
				ValidateSceneNumber(scene.Number);
				ValidateSceneName(scene.Name);

				if (!sceneNumbers.Add(scene.Number))
				{
					throw new ArgumentException("Each FM9 scene can only be authored once.", nameof(scenes));
				}

				HashSet<Fm9RideSceneBlock> blocks = [];

				foreach (Fm9RideSceneBlockState state in scene.BlockStates)
				{
					ArgumentNullException.ThrowIfNull(state);

					if (!records.ContainsKey(state.Block))
					{
						throw new ArgumentException(
							"Each scene state must name one supported existing Ride-template block.",
							nameof(scenes));
					}

					if (!blocks.Add(state.Block))
					{
						throw new ArgumentException(
							"Each supported Ride-template block can only be authored once per scene.",
							nameof(scenes));
					}

					if (state.Channel is < Fm9SceneChannel.A or > Fm9SceneChannel.D)
					{
						throw new ArgumentOutOfRangeException(
							nameof(scenes),
							"An FM9 scene channel must be A, B, C, or D.");
					}

				}

				if (blocks.Count != RideRecords.Count)
				{
					throw new ArgumentException(
						"Each authored FM9 scene must explicitly define channel and bypass state for every supported Ride-template block; omitted state cannot inherit from the template.",
						nameof(scenes));
				}

			}

			return records;
		}

		public static Fm9PresetDump WithReverbBypassed(
			Fm9PresetDump source,
			int sceneNumber)
		{
			ArgumentNullException.ThrowIfNull(source);
			ValidateSceneNumber(sceneNumber);

			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(source);
			Fm9PresetBlockRecord reverb = ResolveRideRecords(body)[Fm9RideSceneBlock.Reverb];
			byte[] bytes = body.Bytes.ToArray();
			WriteUInt16(bytes, GetSceneBypassOffset(reverb, sceneNumber), 1);

			Fm9PresetDump result = Fm9PresetBodyEncoder.Reencode(source, bytes);
			Fm9PresetBody verified = Fm9PresetBodyDecoder.Decode(result);
			Fm9PresetBlockRecord verifiedReverb =
				ResolveRideRecords(verified)[Fm9RideSceneBlock.Reverb];
			Fm9PresetBlockSceneState state =
				GetSceneState(verified, verifiedReverb, sceneNumber);

			if (!state.Bypassed)
			{
				throw new InvalidDataException("The exported FM9 Reverb bypass state did not validate.");
			}

			return result;
		}

		public static Fm9PresetDump Apply(
			Fm9PresetDump source,
			string presetName,
			IReadOnlyList<Fm9RideScene> scenes)
		{
			ArgumentNullException.ThrowIfNull(source);

			Fm9PresetBody body = Fm9PresetBodyDecoder.Decode(source);
			byte[] sceneBody = WithRideScenes(body, scenes);
			Fm9PresetDump exported = Fm9PresetBodyEncoder.Reencode(source, sceneBody)
				.WithPresetName(presetName);
			Fm9PresetDump reparsed = Fm9PresetDump.Parse(exported.ToArray());
			_ = Fm9PresetBodyDecoder.Decode(reparsed);
			return reparsed;
		}

		private static Dictionary<Fm9RideSceneBlock, Fm9PresetBlockRecord> ResolveRideRecords(
			Fm9PresetBody source)
		{
			Dictionary<Fm9RideSceneBlock, Fm9PresetBlockRecord> records = [];
			Fm9BlockRecordTemplateProfile profile =
				Fm9BlockRecordTemplateProfile.CreateRideBaseline(source);

			foreach (KeyValuePair<Fm9RideSceneBlock, (ushort Columns, ushort Rows, int EffectId)> expected in RideRecords)
			{
				Fm9PresetBlockRecord record = profile.GetRecordForExistingBlock(
					source,
					GetBlockType(expected.Key),
					1);
				ValidateRecord(source, record);
				records.Add(expected.Key, record);
			}

			return records;
		}

		// The proposal block types every authored scene must give a channel and bypass state.
		public static IReadOnlyList<string> RideSceneBlockTypes { get; } =
			Enum.GetValues<Fm9RideSceneBlock>().Select(GetBlockType).ToArray();

		private static string GetBlockType(Fm9RideSceneBlock block) => block switch
		{
			Fm9RideSceneBlock.Gate => "Gate/Expander",
			Fm9RideSceneBlock.ParametricEq => "Parametric EQ",
			_ => block.ToString()
		};

		private static void ValidateRecord(Fm9PresetBody source, Fm9PresetBlockRecord record)
		{
			// Scene words sit in the record header whatever its row count (1-row Send/Return, 2-row Megatap and 6-row
			// Multiplexer decode to the states the FM9's status dump reports).
			if (record.Offset < sizeof(ushort) ||
				record.Offset + record.Length > source.Bytes.Length ||
				record.ParameterRows < 1)
			{
				throw new InvalidDataException(
					"The FM9 scene record is not a serialized block record.");
			}
		}

		private static int GetSceneChannelOffset(Fm9PresetBlockRecord record, int sceneNumber)
		{
			return record.Offset + ((SceneChannelFirstHeaderWord + sceneNumber - 1) * sizeof(ushort));
		}

		private static int GetSceneBypassOffset(Fm9PresetBlockRecord record, int sceneNumber)
		{
			return record.Offset + ((SceneBypassFirstHeaderWord + sceneNumber - 1) * sizeof(ushort));
		}

		private static void ValidateSceneNumber(int sceneNumber)
		{
			if (sceneNumber is < 1 or > SceneCount)
			{
				throw new ArgumentOutOfRangeException(
					nameof(sceneNumber),
					"An FM9 scene number must be from 1 through 8.");
			}
		}

		public static void ValidateSceneName(string name)
		{
			if (string.IsNullOrWhiteSpace(name) || name.Length > 31 ||
				name.Any(character => character is < ' ' or > '~'))
			{
				throw new ArgumentException(
					"An FM9 scene name must contain 1 to 31 printable ASCII characters.",
					nameof(name));
			}
		}

		private static void WriteSceneName(byte[] bytes, int sceneNumber, string name)
		{
			int offset = SceneNameOffset + ((sceneNumber - 1) * SceneNameLength);
			Array.Clear(bytes, offset, SceneNameLength);
			Encoding.ASCII.GetBytes(name).CopyTo(bytes, offset);
		}

		private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset)
		{
			return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
		}

		private static void WriteUInt16(byte[] bytes, int offset, ushort value)
		{
			bytes[offset] = (byte)value;
			bytes[offset + 1] = (byte)(value >> 8);
		}
	}
}
