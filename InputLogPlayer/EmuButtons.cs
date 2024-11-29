// Copyright (c) 2024 CasualPokePlayer
// SPDX-License-Identifier: MPL-2.0

using System;

namespace InputLogPlayer;

[Flags]
public enum EmuButtons : uint
{
	A = 1 << 0,
	B = 1 << 1,
	Select = 1 << 2,
	Start = 1 << 3,
	Right = 1 << 4,
	Left = 1 << 5,
	Up = 1 << 6,
	Down = 1 << 7,
	R = 1 << 8,
	L = 1 << 9,

	GB_BUTTON_MASK = A | B | Select | Start | Right | Left | Up | Down,
	GBA_BUTTON_MASK = A | B | Select | Start | Right | Left | Up | Down | R | L,

	HardReset = 1u << 31,
}

