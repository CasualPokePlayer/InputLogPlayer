// Copyright (c) 2024 CasualPokePlayer
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace InputLogPlayer.Cores;

internal static partial class MGBA
{
	/// <summary>
	/// Create opaque state
	/// </summary>
	/// <param name="romData">the rom data, can be disposed of once this function returns</param>
	/// <param name="romLength">length of romData in bytes</param>
	/// <param name="biosData">the bios data, can be disposed of once this function returns</param>
	/// <param name="biosLength">length of biosData in bytes</param>
	/// <param name="forceDisableRtc">force disable rtc, if present</param>
	/// <returns>opaque state pointer</returns>
	[LibraryImport("mgba")]
	[UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
	public static partial nint mgba_create(ReadOnlySpan<byte> romData, int romLength, ReadOnlySpan<byte> biosData, int biosLength, [MarshalAs(UnmanagedType.U1)] bool forceDisableRtc);

	/// <param name="core">opaque state pointer</param>
	[LibraryImport("mgba")]
	[UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
	public static partial void mgba_destroy(nint core);

	/// <summary>
	/// set color palette lookup
	/// </summary>
	/// <param name="core">opaque state pointer</param>
	/// <param name="colorLut">uint32[32768], input color (r,g,b) is at lut[r | g &lt;&lt; 5 | b &lt;&lt; 10]</param>
	[LibraryImport("mgba")]
	[UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
	public static partial void mgba_setcolorlut(nint core, ReadOnlySpan<uint> colorLut);

	/// <summary>
	/// combination of button flags used in mgba_advance
	/// </summary>
	[Flags]
	public enum Buttons : ushort
	{
		A = 0x001,
		B = 0x002,
		SELECT = 0x004,
		START = 0x008,
		RIGHT = 0x010,
		LEFT = 0x020,
		UP = 0x040,
		DOWN = 0x080,
		R = 0x0100,
		L = 0x0200,
	}

	/// <summary>
	/// Emulates one frame
	/// </summary>
	/// <param name="core">opaque state pointer</param>
	/// <param name="buttons">input for this frame</param>
	/// <param name="videoBuf">240x160 ARGB32 (native endian) video frame buffer</param>
	/// <param name="soundBuf">buffer with at least 1024 stereo samples (2048 16-bit integers)</param>
	/// <param name="samples">number of stereo samples produced (double this to get 16-bit integer count)</param>
	/// <param name="cpuCycles">number of cpu cycles advanced</param>
	[LibraryImport("mgba")]
	[UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
	public static partial void mgba_advance(nint core, Buttons buttons, [Out] uint[] videoBuf, [Out] short[] soundBuf, out uint samples, out uint cpuCycles);

	/// <summary>
	/// Reset to initial state.
	/// Equivalent to reloading a ROM image, or turning a Game Boy Advance off and on again.
	/// </summary>
	/// <param name="core">opaque state pointer</param>
	[LibraryImport("mgba")]
	[UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
	public static partial void mgba_reset(nint core);

	/// <summary>
	/// restore persistant cart memory.
	/// </summary>
	/// <param name="core">opaque state pointer</param>
	/// <param name="data">byte buffer to read from. mgba_getsavedatalength() bytes will be read</param>
	[LibraryImport("mgba")]
	[UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
	public static partial void mgba_loadsavedata(nint core, ReadOnlySpan<byte> data);

	/// <summary>
	/// Loads emulator state from the buffer given by 'stateBuf' of size 'size'.
	/// </summary>
	/// <param name="core">opaque state pointer</param>
	/// <param name="stateBuf">buffer for savestate</param>
	/// <param name="size">size of savestate buffer</param>
	/// <returns>success</returns>
	[LibraryImport("mgba")]
	[UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
	[return: MarshalAs(UnmanagedType.U1)]
	public static partial bool mgba_loadstate(nint core, ReadOnlySpan<byte> stateBuf, int size);
}
