// Copyright (c) 2024 CasualPokePlayer
// SPDX-License-Identifier: MPL-2.0

using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using InputLogPlayer.Audio;
using InputLogPlayer.Cores;

using static SDL3.SDL;

namespace InputLogPlayer;

internal static class InputLogPlayer
{
	private static readonly Option<string> _romOption = new(name: "--rom")
	{
		Description = "Path to ROM to be loaded",
		Arity = ArgumentArity.ExactlyOne,
		Required = true
	};

	private static readonly Option<string> _biosOption = new(name: "--bios")
	{
		Description = "Path to BIOS to be loaded",
		Arity = ArgumentArity.ExactlyOne,
		Required = true
	};

	private static readonly Option<string> _gm2Option = new(name: "--gm2")
	{
		Description = "Path to .gm2 to be played back",
		Arity = ArgumentArity.ExactlyOne,
		Required = true
	};

	private static readonly Option<bool> _unthrottledOption = new(name: "--unthrottled")
	{
		Description = "Run at maximum speed",
		Arity = ArgumentArity.Zero,
	};

	private static readonly Option<bool> _dumpAvOption = new(name: "--dump-av")
	{
		Description = "Dump audio/video as an MP4, usually used with --unthrottled",
		Arity = ArgumentArity.Zero
	};

	private static readonly Option<bool> _convertGbiOption = new(name: "--convert-gbi")
	{
		Description = "Convert gm2 to a GBI input log and exit immediately",
		Arity = ArgumentArity.Zero
	};

	private static bool IsQuitEvent(in SDL_Event sdlEvent)
	{
		return (SDL_EventType)sdlEvent.type is SDL_EventType.SDL_EVENT_QUIT or SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED;
	}

	[UnmanagedCallersOnly(CallConvs = [ typeof(CallConvCdecl) ])]
	private static unsafe SDLBool SDLEventFilter(nint userdata, SDL_Event* sdlEvent)
	{
		return IsQuitEvent(in *sdlEvent);
	}

	private const int TIMER_FIXED_SHIFT = 15;
	private static readonly long _timerFreq = Stopwatch.Frequency << TIMER_FIXED_SHIFT;

	private static UInt128 _lastTime = (UInt128)Stopwatch.GetTimestamp() << TIMER_FIXED_SHIFT;
	private static long _throttleError;

	private static void Throttle(uint cpuCycles, uint cpuFreq)
	{
		// same as cpuCycles / cpuFreq * timerFreq, but avoids needing to use float math
		// note that Stopwatch.Frequency is typically 10MHz on Windows, and always 1000MHz on non-Windows
		// (ulong cast is needed here, due to the amount of cpu cycles a gba could produce)
		var timeToThrottle = (long)((ulong)_timerFreq * cpuCycles / cpuFreq);
		if (_throttleError >= timeToThrottle)
		{
			_throttleError -= timeToThrottle;
			return;
		}

		timeToThrottle -= _throttleError;

		while (true)
		{
			var curTime = (UInt128)Stopwatch.GetTimestamp() << TIMER_FIXED_SHIFT;
			var elaspedTime = (long)(curTime - _lastTime);
			_lastTime = curTime;

			// the time elasped would by the time actually spent sleeping
			// also would be the time spent emulating, which we want to discount for throttling obviously

			if (elaspedTime >= timeToThrottle)
			{
				_throttleError = elaspedTime - timeToThrottle;
				break;
			}

			timeToThrottle -= elaspedTime;

			var timeToThrottleMs = timeToThrottle * 1000 / _timerFreq;
			// if we're under 1 ms, don't throttle, leave it for the next time
			if (timeToThrottleMs < 1)
			{
				_throttleError = -timeToThrottle;
				break;
			}

			// we'll likely oversleep by at least a millisecond, so reduce throttle time by 1 ms
			// note that Thread.Sleep(0) is the same as Thread.Yield() (which we want in that case)
			Thread.Sleep((int)(timeToThrottleMs - 1));
		}
	}

	private static unsafe void RenderVideo(ReadOnlySpan<uint> videoBuffer, int videoHeight, int videoPitch, nint sdlTexture, nint sdlRenderer)
	{
		if (!SDL_LockTexture(sdlTexture, ref Unsafe.NullRef<SDL_Rect>(), out var pixels, out var pitch))
		{
			throw new($"Failed to lock SDL texture, SDL error {SDL_GetError()}");
		}

		try
		{
			if (pitch == videoPitch) // identical pitch, fast case (probably always the case?)
			{
				videoBuffer.CopyTo(new((void*)pixels, videoBuffer.Length));
			}
			else // different pitch, slow case (indicates padding between lines)
			{
				var videoBufferAsBytes = MemoryMarshal.AsBytes(videoBuffer);
				for (var i = 0; i < videoHeight; i++)
				{
					videoBufferAsBytes.Slice(i * videoPitch, videoPitch)
						.CopyTo(new((void*)(pixels + i * pitch), videoPitch));
				}
			}
		}
		finally
		{
			SDL_UnlockTexture(sdlTexture);
		}

		_ = SDL_RenderTexture(sdlRenderer, sdlTexture, ref Unsafe.NullRef<SDL_FRect>(), ref Unsafe.NullRef<SDL_FRect>()); 
		_ = SDL_RenderPresent(sdlRenderer);
	}

	private static int Main(string[] args)
	{
		_romOption.AcceptLegalFileNamesOnly();
		_biosOption.AcceptLegalFileNamesOnly();
		_gm2Option.AcceptLegalFileNamesOnly();

		var root = new RootCommand(description: "GSE Input Log Player")
		{
			_romOption,
			_biosOption,
			_gm2Option,
			_unthrottledOption,
			_dumpAvOption,
			_convertGbiOption
		};

		// Remove version option (doesn't make sense here)
		for (var i = 0; i < root.Options.Count; i++)
		{
			if (root.Options[i] is VersionOption)
			{
				root.Options.RemoveAt(i--);
			}
		}

		var result = CommandLineParser.Parse(root, args);
		var invokeResult = result.Invoke();
		if (invokeResult != 0 || result.Action != null)
		{
			return invokeResult;
		}

		var romPath = result.GetRequiredValue(_romOption);
		var biosPath = result.GetRequiredValue(_biosOption);
		var gm2Path = result.GetRequiredValue(_gm2Option);
		var unthrottled = result.GetValue(_unthrottledOption);
		var dumpAv = result.GetValue(_dumpAvOption);
		var convertGbi = result.GetValue(_convertGbiOption);

		if (convertGbi)
		{
			using var emuInputLog = new EmuInputLog(gm2Path);

			if (emuInputLog.Platform is not (EmuInputLog.EmuPlatform.GBC_GBA or EmuInputLog.EmuPlatform.GBA))
			{
				Console.Error.WriteLine("GBI input logs require GBC in GBA or GBA.");
				return -1;
			}

			if (emuInputLog.StartsFromSavestate)
			{
				Console.Error.WriteLine("GBI input logs require starting from Power-On.");
				return -1;
			}

			{
				using var startSav = File.Create(Path.ChangeExtension(gm2Path, ".sav"));
				startSav.Write(emuInputLog.StateOrSave.Span);
			}

			using var gbiInputLog = new StreamWriter(Path.ChangeExtension(gm2Path, ".txt"));
			var gbiCycleCount = 0UL;
			while (true)
			{
				var movieInput = emuInputLog.GetNextInput();
				if (!movieInput.HasValue)
				{
					break;
				}

				if (movieInput.Value.HardReset)
				{
					Console.WriteLine("Hard reset detected, this is not supported by GBI input logs. Ending conversion.");
					break;
				}

				var gbiCyclesRan = movieInput.Value.CpuCyclesRan;
				if (emuInputLog.Platform is EmuInputLog.EmuPlatform.GBC_GBA)
				{
					// convert 2MiHz to 16MiHz
					gbiCyclesRan *= 8;
				}

				gbiCycleCount += gbiCyclesRan;
				gbiInputLog.WriteLine($"{gbiCycleCount:X8} {(uint)movieInput.Value.GBAInputState:X4}");
			}

			return 0;
		}

		if (dumpAv)
		{
			if (!FFmpegDumper.IsAvailable)
			{
				Console.Error.WriteLine("FFmpeg executable cannot be found. A/V dumping requires FFmpeg.");
				Console.Error.WriteLine("Visit https://ffmpeg.org/download.html to download FFmpeg.");
				return -1;
			}
		}

		using var emuCore = EmuCoreFactory.CreateEmuCore(romPath, biosPath, gm2Path);

		unsafe
		{
			SDL_SetEventFilter(&SDLEventFilter, 0); // filter out events which we don't care for
		}

		if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO | SDL_InitFlags.SDL_INIT_EVENTS))
		{
			throw new($"Could not init SDL video! SDL error: {SDL_GetError()}");
		}

		nint sdlWindow = 0, sdlRenderer = 0, emuTexture = 0;
		try
		{
			const SDL_WindowFlags windowFlags = SDL_WindowFlags.SDL_WINDOW_HIDDEN;
			sdlWindow = SDL_CreateWindow("Input Log Player", emuCore.VideoWidth * 3, emuCore.VideoHeight * 3, windowFlags);
			if (sdlWindow == 0)
			{
				throw new($"Could not create SDL window! SDL error: {SDL_GetError()}");
			}

			sdlRenderer = SDL_CreateRenderer(sdlWindow, null);
			if (sdlRenderer == 0)
			{
				throw new($"Could not create SDL renderer! SDL error: {SDL_GetError()}");
			}

			if (!SDL_SetRenderVSync(sdlRenderer, 0))
			{
				throw new($"Could not disable vsync for SDL renderer! SDL error: {SDL_GetError()}");
			}

			unsafe
			{
				emuTexture = (nint)SDL_CreateTexture(sdlRenderer, SDL_PixelFormat.SDL_PIXELFORMAT_ARGB8888,
					SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING, emuCore.VideoWidth, emuCore.VideoHeight);
			}

			if (emuTexture == 0)
			{
				throw new($"Failed to create video texture, SDL error: {SDL_GetError()}");
			}

			if (!SDL_SetTextureScaleMode(emuTexture, SDL_ScaleMode.SDL_SCALEMODE_NEAREST))
			{
				throw new($"Failed to set texture scaling mode, SDL error: {SDL_GetError()}");
			}

			if (!SDL_SetTextureBlendMode(emuTexture, SDL_BLENDMODE_NONE))
			{
				throw new($"Failed to set texture blend mode, SDL error: {SDL_GetError()}");
			}

			SDL_ShowWindow(sdlWindow);

			var pumpRate = Stopwatch.Frequency / 20;
			var nextPumpTime = Stopwatch.GetTimestamp();
			var videoHeight = emuCore.VideoHeight;
			var videoPitch = emuCore.VideoWidth * sizeof(uint);
			using var audioManager = new AudioManager(emuCore.AudioFrequency, unthrottled);
			using var avDumper = new FFmpegDumper(gm2Path, emuCore.AudioFrequency, emuCore.VideoWidth, videoHeight, dumpAv);

			while (true)
			{
				emuCore.Advance(out var completedFrame, out var numSamples, out var cpuCycles);

				if (dumpAv)
				{
					if (completedFrame)
					{
						avDumper.WriteVideoFrame(emuCore.VideoBuffer);
					}

					avDumper.WriteAudioFrame(emuCore.AudioBuffer[..(int)(numSamples * 2)]);
				}

				if (!unthrottled)
				{
					audioManager.DispatchAudio(emuCore.AudioBuffer[..(int)(numSamples * 2)]);

					if (completedFrame)
					{
						RenderVideo(emuCore.VideoBuffer, videoHeight, videoPitch, emuTexture, sdlRenderer);
					}

					Throttle(cpuCycles, emuCore.CpuFrequency);
				}

				// pump events periodically
				if (Stopwatch.GetTimestamp() >= nextPumpTime)
				{
					// also render video here if we're unthrottled
					if (unthrottled && completedFrame)
					{
						RenderVideo(emuCore.VideoBuffer, videoHeight, videoPitch, emuTexture, sdlRenderer);
					}

					while (SDL_PollEvent(out var sdlEvent))
					{
						if (IsQuitEvent(in sdlEvent))
						{
							return 0;
						}
					}

					nextPumpTime = Stopwatch.GetTimestamp() + pumpRate;
				}
			}
		}
		finally
		{
			SDL_DestroyTexture(emuTexture);
			SDL_DestroyRenderer(sdlRenderer);
			SDL_DestroyWindow(sdlWindow);
			SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_VIDEO | SDL_InitFlags.SDL_INIT_EVENTS);
			SDL_Quit();
		}
	}
}
