// Copyright (c) 2024 CasualPokePlayer
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using ZstdDecompressionStream = ZstdSharp.DecompressionStream;

namespace InputLogPlayer;

/// <summary>
/// Implements an emu input log, one which could be played back with external tooling
/// This input log is intended to record GB/C/A inputs, along with various emu actions (e.g. save/state loads)
/// It uses the .gm2 extension, as it is an effective successor to Gambatte-Speedrun's .gm format
/// </summary>
internal sealed class EmuInputLog : IDisposable
{
	public enum EmuPlatform : uint
	{
		GB,
		GBC,
		GBC_GBA,
		SGB2,
		GBA,
	}

	[Flags]
	public enum MovieFlags : uint
	{
		/// <summary>
		/// Movie starts from a savestate, rather than power-on + save file
		/// </summary>
		StartsFromSaveState = 1 << 0,

		/// <summary>
		/// Data after the header is zstd compressed
		/// All movies made in GSE are zstd compressed
		/// </summary>
		IsZstdCompressed = 1 << 1,

		/// <summary>
		/// GBA RTC should be force disabled
		/// </summary>
		GbaRtcDisabled = 1 << 2,
	}

	/// <summary>
	/// Strings within the input log header comprise of 1 byte for length and a 255 byte buffer to hold UTF8 chars
	/// Strings which exceed these limitations must be truncated
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	private struct HeaderString
	{
		public byte Length;
		public unsafe fixed byte Buffer[255];

		public unsafe string GetString()
		{
			fixed (byte* buffer = Buffer)
			{
				return Encoding.UTF8.GetString(buffer, Length);
			}
		}
	}

	private const int GM2_VERSION = 2;
	private const ulong GM2_MAGIC = 0x4753454D4F564945;

	[StructLayout(LayoutKind.Sequential, Size = 1024)]
	private struct EmuInputLogHeader
	{
		/// <summary>
		/// Input log signature, to mark this is a .gm2
		/// Should always be 0x4753454D4F564945 in big endian
		/// (i.e. GSEMOVIE)
		/// </summary>
		public ulong InputLogMagic;

		/// <summary>
		/// Input log version, increased on any movie format change.
		/// </summary>
		public uint InputLogVersion;

		/// <summary>
		/// The emu platform. This defines the platform which should be chosen for the emu core.
		/// For GB/C games, GB, GBC, GBC in GBA, and SGB2 are valid modes.
		/// For GBA games, only GBA is a valid mode.
		/// </summary>
		public EmuPlatform Platform;

		/// <summary>
		/// Reset stall for GB/C games. Should be ignored for GBA games
		/// </summary>
		public uint ResetStall;

		/// <summary>
		/// Movie flags. Generally used for movie quirks and movie sync settings
		/// </summary>
		public MovieFlags Flags;

		/// <summary>
		/// Unix timestamp when this movie was started
		/// </summary>
		public long StartTimestamp;

		/// <summary>
		/// GB/C RTC dividers (2^21/sec) for movie sync, appropriate for gambatte_settime
		/// Should be ignored for movies which start from a savestate 
		/// </summary>
		public ulong GbRtcDividers;

		/// <summary>
		/// The starting savestate or save file size
		/// If the movie starts from a savestate, the savestate will proceed after the movie header
		/// If the movie starts from power-on + save file, the save file will proceed after the movie header
		/// Note this size is for the uncompressed data, not the potentially compressed data
		/// </summary>
		public uint StateOrSaveSize;

		/// <summary>
		/// The ROM file name, without the extension
		/// </summary>
		public HeaderString RomName;

		/// <summary>
		/// GSE version string
		/// </summary>
		public HeaderString EmuVersion;

		/// <summary>
		/// GBA RTC time as a unix timestamp, used for mgba_create/mgba_loadsavedata/mgba_loadstate
		/// For movies which start from a savestate, this is more a backup in case the savestate is missing RTC data 
		/// </summary>
		public long GbaRtcTime;

		/// <summary>
		/// Normalizes little endian to native endianness
		/// (Except for magic, which is normally big endian)
		/// </summary>
		public void NormalizeEndianness()
		{
			if (!BitConverter.IsLittleEndian)
			{
				InputLogVersion = BinaryPrimitives.ReverseEndianness(InputLogVersion);
				Platform = (EmuPlatform)BinaryPrimitives.ReverseEndianness((uint)Platform);
				ResetStall = BinaryPrimitives.ReverseEndianness(ResetStall);
				Flags = (MovieFlags)BinaryPrimitives.ReverseEndianness((uint)Flags);
				StartTimestamp = BinaryPrimitives.ReverseEndianness(StartTimestamp);
				GbRtcDividers = BinaryPrimitives.ReverseEndianness(GbRtcDividers);
				StateOrSaveSize = BinaryPrimitives.ReverseEndianness(StateOrSaveSize);
				GbaRtcTime = BinaryPrimitives.ReverseEndianness(GbaRtcTime);
			}
			else
			{
				InputLogMagic = BinaryPrimitives.ReverseEndianness(InputLogMagic);
			}
		}
	}

	private readonly FileStream _gm2File;
	private readonly EmuInputLogHeader _header;
	// all data after the header is compressed with zstd
	private readonly Stream _inputStream;
	private readonly BinaryReader _inputReader;
	private bool _movieEnded;

	public readonly record struct MovieInput(uint CpuCyclesRan, EmuButtons InputState)
	{
		public EmuButtons GBInputState => InputState & EmuButtons.GB_BUTTON_MASK;

		public EmuButtons GBAInputState => InputState & EmuButtons.GBA_BUTTON_MASK;

		public bool HardReset => (InputState & EmuButtons.HardReset) != 0;
	}

	public EmuPlatform Platform => _header.Platform;
	public uint ResetStall => _header.ResetStall;
	public bool StartsFromSavestate => (_header.Flags & MovieFlags.StartsFromSaveState) != 0;
	public bool GbaRtcDisabled => (_header.Flags & MovieFlags.GbaRtcDisabled) != 0;
	public ulong GbRtcDividers => _header.GbRtcDividers;
	public long GbaRtcTime => _header.GbaRtcTime;
	public ReadOnlyMemory<byte> StateOrSave { get; }

	public EmuInputLog(string gm2Path)
	{
		try
		{
			_gm2File = File.OpenRead(gm2Path);

			_gm2File.ReadExactly(MemoryMarshal.AsBytes<EmuInputLogHeader>(new(ref _header)));
			_header.NormalizeEndianness();

			if (_header.InputLogMagic != GM2_MAGIC)
			{
				throw new("Wrong gm2 magic!");
			}

			if (_header.InputLogVersion != GM2_VERSION)
			{
				throw new("Incompatible gm2 version!");
			}

			if (!Enum.IsDefined(Platform))
			{
				throw new("Invalid emu platform!");
			}

			var isZstdCompressed = (_header.Flags & MovieFlags.IsZstdCompressed) != 0;
			if (isZstdCompressed)
			{
				_inputStream = new ZstdDecompressionStream(_gm2File);
			}
			else
			{
				_inputStream = _gm2File;
			}

			var stateOrSave = new byte[_header.StateOrSaveSize];
			_inputStream.ReadExactly(stateOrSave);
			StateOrSave = stateOrSave;

			Console.WriteLine($"Emu Platform: {Platform}");
			if (Platform != EmuPlatform.GBA)
			{
				Console.WriteLine($"Reset Stall: {ResetStall}");
			}

			Console.WriteLine($"Starts from savestate: {StartsFromSavestate}");
			Console.WriteLine($"Is Compressed: {isZstdCompressed}");
			if (Platform == EmuPlatform.GBA)
			{
				Console.WriteLine($"GBA RTC Disabled: {GbaRtcDisabled}");
			}

			var creationTime = DateTime.UnixEpoch.AddSeconds(_header.StartTimestamp);
			Console.WriteLine($"Creation time: {creationTime}");

			Console.WriteLine(Platform != EmuPlatform.GBA
				? $"GB RTC Dividers: {GbRtcDividers}"
				: $"GBA RTC Time: {GbaRtcTime}");

			var stateOrSaveStr = StartsFromSavestate ? "State" : "Save";
			Console.WriteLine($"{stateOrSaveStr} Size: {_header.StateOrSaveSize}");

			Console.WriteLine($"Used ROM Name: {_header.RomName.GetString()}");
			Console.WriteLine($"Used Emu version: {_header.EmuVersion.GetString()}");

			_inputReader = new(_inputStream);
		}
		catch
		{
			Dispose();
			throw;
		}
	}

	public MovieInput? GetNextInput()
	{
		if (_movieEnded)
		{
			return null;
		}

		try
		{
			var cpuCyclesRan = _inputReader.ReadUInt32();
			var emuButtons = (EmuButtons)_inputReader.ReadUInt32();
			return new(cpuCyclesRan, emuButtons);
		}
		catch (EndOfStreamException)
		{
			Console.WriteLine("Movie ended");
			_movieEnded = true;
			return null;
		}
	}

	public void Dispose()
	{
		_inputReader?.Dispose();
		_inputStream?.Dispose();
		_gm2File?.Dispose();
	}
}
