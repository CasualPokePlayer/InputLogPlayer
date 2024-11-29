// Copyright (c) 2024 CasualPokePlayer
// SPDX-License-Identifier: MPL-2.0

using System;

namespace InputLogPlayer.Cores;

internal interface IEmuCore : IDisposable
{
	void Advance(out bool completedFrame, out uint samples, out uint cpuCycles);

	ReadOnlySpan<uint> VideoBuffer { get; }
	int VideoWidth { get; }
	int VideoHeight { get; }

	ReadOnlySpan<short> AudioBuffer { get; }
	int AudioFrequency { get; }

	uint CpuFrequency { get; }
}
