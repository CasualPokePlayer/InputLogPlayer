// Copyright (c) 2024 CasualPokePlayer
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using static InputLogPlayer.Cores.Gambatte;
	
namespace InputLogPlayer.Cores;

internal sealed class GambatteCore : IEmuCore
{
	private readonly nint _opaque;

	private readonly uint[] _videoBuffer = new uint[160 * 144];
	private readonly uint[] _audioBuffer = new uint[35112 + 2064];

	private GCHandle _inputGetterUserData;

	private readonly EmuInputLog _emuInputLog;

	private Buttons CurrentButtons;

	[UnmanagedCallersOnly(CallConvs = [ typeof(CallConvCdecl) ])]
	private static Buttons InputGetter(nint userdata)
	{
		var core = (GambatteCore)GCHandle.FromIntPtr(userdata).Target!;
		return core.CurrentButtons;
	}

	public GambatteCore(byte[] romData, byte[] biosData, EmuInputLog emuInputLog)
	{
		_emuInputLog = emuInputLog;

		try
		{
			_opaque = gambatte_create();
			if (_opaque == 0)
			{
				throw new("Failed to create core opaque state!");
			}

			var loadFlags = emuInputLog.Platform switch
			{
				EmuInputLog.EmuPlatform.GB => LoadFlags.READONLY_SAV,
				EmuInputLog.EmuPlatform.GBC => LoadFlags.CGB_MODE | LoadFlags.READONLY_SAV,
				EmuInputLog.EmuPlatform.GBC_GBA => LoadFlags.CGB_MODE | LoadFlags.GBA_FLAG | LoadFlags.READONLY_SAV,
				EmuInputLog.EmuPlatform.SGB2 => LoadFlags.SGB_MODE | LoadFlags.READONLY_SAV,
				EmuInputLog.EmuPlatform.GBA => throw new("EmuPlatform.GBA is for GBA games"),
				_ => throw new InvalidOperationException(),
			};

			var loadRes = gambatte_loadbuf(_opaque, romData, (uint)romData.Length, loadFlags);
			if (loadRes != 0)
			{
				throw new($"Failed to load ROM! Core returned {loadRes}");
			}

			if (emuInputLog.Platform == EmuInputLog.EmuPlatform.GBC_GBA)
			{
				biosData[0xF3] ^= 0x03;
				Buffer.BlockCopy(biosData, 0xF6, biosData, 0xF5, 0xFB - 0xF5);
				biosData[0xFB] ^= 0x74;
			}

			loadRes = gambatte_loadbiosbuf(_opaque, biosData, (uint)biosData.Length);
			if (loadRes != 0)
			{
				throw new($"Failed to load BIOS! Core returned {loadRes}");
			}

			_inputGetterUserData = GCHandle.Alloc(this, GCHandleType.Weak);

			unsafe
			{
				gambatte_setinputgetter(_opaque, &InputGetter, GCHandle.ToIntPtr(_inputGetterUserData));
			}

			gambatte_setcgbpalette(_opaque, GBColors.GetLut(emuInputLog.Platform));

			if (_emuInputLog.StartsFromSavestate)
			{
				if (!gambatte_loadstate(_opaque, _emuInputLog.StateOrSave.Span, _emuInputLog.StateOrSave.Length))
				{
					throw new("Failed to load savestate!");
				}
			}
			else
			{
				var savLength = gambatte_getsavedatalength(_opaque);
				if (savLength > 0)
				{
					var savBuffer = new byte[savLength];
					_emuInputLog.StateOrSave.Span.CopyTo(savBuffer);
					gambatte_loadsavedata(_opaque, savBuffer);
				}

				gambatte_settime(_opaque, _emuInputLog.GbRtcDividers);
			}
		}
		catch
		{
			Dispose();
			throw;
		}	
	}

	public void Dispose()
	{
		if (_opaque != 0)
		{
			gambatte_destroy(_opaque);
		}

		if (_inputGetterUserData.IsAllocated)
		{
			_inputGetterUserData.Free();
		}

		_emuInputLog.Dispose();
	}

	public void Advance(out bool completedFrame, out uint samples, out uint cpuCycles)
	{
		var nextInput = _emuInputLog.GetNextInput();
		uint samplesToRun;
		int frameCompletedSample;
		if (!nextInput.HasValue)
		{
			CurrentButtons = 0;
			samplesToRun = 35112;
			frameCompletedSample = gambatte_runfor(_opaque, _videoBuffer, VideoWidth, _audioBuffer, ref samplesToRun);
			completedFrame = frameCompletedSample != -1;
			samples = samplesToRun;
			cpuCycles = samplesToRun;
			return;
		}

		var movieInput = nextInput.Value;
		if (movieInput.HardReset)
		{
			gambatte_reset(_opaque, _emuInputLog.ResetStall);
		}

		if (movieInput.CpuCyclesRan == 0)
		{
			completedFrame = false;
			samples = 0;
			cpuCycles = 0;
			return;
		}

		CurrentButtons = (Buttons)movieInput.GBInputState;
		samplesToRun = movieInput.CpuCyclesRan;

		frameCompletedSample = gambatte_runfor(_opaque, _videoBuffer, VideoWidth, _audioBuffer, ref samplesToRun);

		completedFrame = frameCompletedSample != -1;
		samples = samplesToRun;
		cpuCycles = samplesToRun;
	}

	public ReadOnlySpan<uint> VideoBuffer => _videoBuffer;
	public int VideoWidth => 160;
	public int VideoHeight => 144;

	public ReadOnlySpan<short> AudioBuffer => MemoryMarshal.Cast<uint, short>(_audioBuffer);
	public int AudioFrequency => 2097152;

	public uint CpuFrequency => 2097152;
}
