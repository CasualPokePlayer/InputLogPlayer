// Copyright (c) 2024 CasualPokePlayer
// SPDX-License-Identifier: MPL-2.0

using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using InputLogPlayer.Audio;
using InputLogPlayer.Cores;

using static SDL2.SDL;

namespace InputLogPlayer;

internal static class InputLogPlayer
{
	private static readonly Option<string> _romOption = new(name: "--rom", description: "Path to ROM to be loaded") { Arity = ArgumentArity.ExactlyOne, IsRequired = true };
	private static readonly Option<string> _biosOption = new(name: "--bios", description: "Path to BIOS to be loaded") { Arity = ArgumentArity.ExactlyOne, IsRequired = true };
	private static readonly Option<string> _gm2Option = new(name: "--gm2", description: "Path to .gm2 to be played back") { Arity = ArgumentArity.ExactlyOne, IsRequired = true };
	private static readonly Option<bool> _unthrottledOption = new(name: "--unthrottled", description: "Run at maximum speed") { Arity = ArgumentArity.Zero };
	private static readonly Option<bool> _dumpAvOption = new(name: "--dump-av", description: "Dump audio/video as an MP4, usually used with --unthrottled") { Arity = ArgumentArity.Zero };

	private static bool IsQuitEvent(in SDL_Event sdlEvent)
	{
		return sdlEvent.type is SDL_EventType.SDL_QUIT ||
		       sdlEvent is { type: SDL_EventType.SDL_WINDOWEVENT, window.windowEvent: SDL_WindowEventID.SDL_WINDOWEVENT_CLOSE };
	}

	[UnmanagedCallersOnly(CallConvs = [ typeof(CallConvCdecl) ])]
	private static unsafe int SDLEventFilter(nint userdata, nint sdlEvent)
	{
		var e = (SDL_Event*)sdlEvent;
		return IsQuitEvent(in *e) ? 1 : 0;
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
		if (SDL_LockTexture(sdlTexture, 0, out var pixels, out var pitch) != 0)
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

		_ = SDL_RenderCopy(sdlRenderer, sdlTexture, 0, 0); 
		SDL_RenderPresent(sdlRenderer);
	}

	private static int Main(string[] args)
	{
		_romOption.LegalFileNamesOnly();
		_biosOption.LegalFileNamesOnly();
		_gm2Option.LegalFileNamesOnly();

		var root = new RootCommand
		{
			_romOption,
			_biosOption,
			_gm2Option,
			_unthrottledOption,
			_dumpAvOption
		};

		var helpUsed = false;
		var parser = new CommandLineBuilder(root)
			.UseHelp(_ => helpUsed = true)
			.UseParseDirective()
			.UseSuggestDirective()
			.UseTypoCorrections()
			.UseParseErrorReporting()
			.UseExceptionHandler()
			.Build();

		var result = parser.Parse(args);
		var invokeResult = result.Invoke();
		if (invokeResult != 0 || helpUsed)
		{
			return invokeResult;
		}

		var romPath = result.GetValueForOption(_romOption);
		var biosPath = result.GetValueForOption(_biosOption);
		var gm2Path = result.GetValueForOption(_gm2Option);
		var unthrottled = result.GetValueForOption(_unthrottledOption);
		var dumpAv = result.GetValueForOption(_dumpAvOption);

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

		SDL_SetHint("SDL_WINDOWS_DPI_SCALING", "1");

		if (SDL_Init(SDL_INIT_VIDEO | SDL_INIT_EVENTS) != 0)
		{
			throw new($"Could not init SDL video! SDL error: {SDL_GetError()}");
		}

		nint sdlWindow = 0, sdlRenderer = 0, emuTexture = 0;
		try
		{
			const SDL_WindowFlags windowFlags = SDL_WindowFlags.SDL_WINDOW_ALLOW_HIGHDPI | SDL_WindowFlags.SDL_WINDOW_HIDDEN;
			sdlWindow = SDL_CreateWindow("Input Log Player", SDL_WINDOWPOS_UNDEFINED, SDL_WINDOWPOS_UNDEFINED, emuCore.VideoWidth * 3, emuCore.VideoHeight * 3, windowFlags);
			if (sdlWindow == 0)
			{
				throw new($"Could not create SDL window! SDL error: {SDL_GetError()}");
			}

			sdlRenderer = SDL_CreateRenderer(sdlWindow, -1, SDL_RendererFlags.SDL_RENDERER_ACCELERATED);
			if (sdlRenderer == 0)
			{
				Console.Error.WriteLine("Default accelerated render driver could not be created, falling back on software render driver.");

				sdlRenderer = SDL_CreateRenderer(sdlWindow, -1, SDL_RendererFlags.SDL_RENDERER_SOFTWARE);
				if (sdlRenderer == 0)
				{
					throw new($"Could not create SDL renderer! SDL error: {SDL_GetError()}");
				}

				// go back to 1x window size if we're software rendering
				SDL_SetWindowSize(sdlWindow, emuCore.VideoWidth, emuCore.VideoHeight);
			}

			emuTexture = SDL_CreateTexture(sdlRenderer, SDL_PIXELFORMAT_ARGB8888,
				(int)SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING, emuCore.VideoWidth, emuCore.VideoHeight);
			if (emuTexture == 0)
			{
				throw new($"Failed to create video texture, SDL error: {SDL_GetError()}");
			}

			if (SDL_SetTextureScaleMode(emuTexture, SDL_ScaleMode.SDL_ScaleModeNearest) != 0)
			{
				throw new($"Failed to set texture scaling mode, SDL error: {SDL_GetError()}");
			}

			if (SDL_SetTextureBlendMode(emuTexture, SDL_BlendMode.SDL_BLENDMODE_NONE) != 0)
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

					while (SDL_PollEvent(out var sdlEvent) != 0)
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
			SDL_QuitSubSystem(SDL_INIT_VIDEO | SDL_INIT_EVENTS);
		}
	}
}
