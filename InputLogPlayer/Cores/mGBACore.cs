// Copyright (c) 2024 CasualPokePlayer
// SPDX-License-Identifier: MPL-2.0

using System;

using static InputLogPlayer.Cores.MGBA;

namespace InputLogPlayer.Cores;

internal sealed class MGBACore : IEmuCore
{
	private readonly nint _opaque;
	private readonly uint[] _videoBuffer = new uint[240 * 160];
	private readonly short[] _audioBuffer = new short[0x2000 * 2];

	private readonly EmuInputLog _emuInputLog;

	public MGBACore(byte[] romData, byte[] biosData, EmuInputLog emuInputLog)
	{
		_emuInputLog = emuInputLog;
		try
		{
			_opaque = mgba_create(romData, romData.Length, biosData, biosData.Length, _emuInputLog.GbaRtcDisabled, _emuInputLog.GbaRtcTime);
			if (_opaque == 0)
			{
				throw new("Failed to create core opaque state!");
			}

			mgba_setcolorlut(_opaque, GBColors.GetLut(EmuInputLog.EmuPlatform.GBA));

			if (_emuInputLog.StartsFromSavestate)
			{
				if (!mgba_loadstate(_opaque, _emuInputLog.StateOrSave.Span, _emuInputLog.StateOrSave.Length, _emuInputLog.GbaRtcTime))
				{
					throw new("Failed to load savestate!");
				}
			}
			else
			{
				mgba_loadsavedata(_opaque, _emuInputLog.StateOrSave.Span, _emuInputLog.StateOrSave.Length, _emuInputLog.GbaRtcTime);
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
			mgba_destroy(_opaque);
		}

		_emuInputLog.Dispose();
	}

	public void Advance(out bool completedFrame, out uint samples, out uint cpuCycles)
	{
		var nextInput = _emuInputLog.GetNextInput();
		if (!nextInput.HasValue)
		{
			mgba_advance(_opaque, 0, _videoBuffer, _audioBuffer, out samples, out cpuCycles);
			completedFrame = true;
			return;
		}

		var movieInput = nextInput.Value;
		if (movieInput.HardReset)
		{
			mgba_reset(_opaque);
		}

		if (movieInput.CpuCyclesRan == 0)
		{
			completedFrame = false;
			samples = 0;
			cpuCycles = 0;
			return;
		}

		mgba_advance(_opaque, (Buttons)movieInput.GBAInputState, _videoBuffer, _audioBuffer, out var samplesRan, out var cpuCyclesRan);
		if (cpuCyclesRan != movieInput.CpuCyclesRan)
		{
			Console.Error.WriteLine($"Possible desync: cpu cycles mismatch");
		}

		completedFrame = true;
		samples = samplesRan;
		cpuCycles = cpuCyclesRan;
	}

	public ReadOnlySpan<uint> VideoBuffer => _videoBuffer;
	public int VideoWidth => 240;
	public int VideoHeight => 160;

	public ReadOnlySpan<short> AudioBuffer => _audioBuffer;
	public int AudioFrequency => 262144;

	public uint CpuFrequency => 16777216;
}
