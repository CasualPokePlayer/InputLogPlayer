// Copyright (c) 2024 CasualPokePlayer
// SPDX-License-Identifier: MPL-2.0

using System.IO;

namespace InputLogPlayer.Cores;

internal static class EmuCoreFactory
{
	public static IEmuCore CreateEmuCore(string romPath, string biosPath, string emuInputLogPath)
	{
		var romData = File.ReadAllBytes(romPath);
		var biosData = File.ReadAllBytes(biosPath);
		var emuInputLog = new EmuInputLog(emuInputLogPath);
		return emuInputLog.Platform == EmuInputLog.EmuPlatform.GBA
			? new MGBACore(romData, biosData, emuInputLog)
			: new GambatteCore(romData, biosData, emuInputLog);
	}
}
