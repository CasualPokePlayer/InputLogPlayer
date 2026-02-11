// Copyright (c) 2024 CasualPokePlayer
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using static SDL3.SDL;

namespace InputLogPlayer.Audio;

// NOTE: Previously I tried a threading approach which offloaded resampling to a separate thread...
// That approach was very complicated and audio did not work at all for reasons I can't comprehend (edit: blipbuf managed impl was bugged, fixed now, maybe can reintroduce?)
// Thus, resampling is done on the emu thread, and we let SDL handle obtaining samples in its audio callback (called on a separate thread anyways)
public sealed class AudioManager : IDisposable
{
	private readonly AudioRingBuffer OutputAudioBuffer = new();

	[UnmanagedCallersOnly(CallConvs = [ typeof(CallConvCdecl) ])]
	private static unsafe void SDLAudioCallback(nint userdata, nint stream, int additionalAmount, int totalAmount)
	{
		var manager = (AudioManager)GCHandle.FromIntPtr(userdata).Target!;
		var samples = additionalAmount / 2;
		var sampleBuffer = samples > 2048
			? new short[samples]
			: stackalloc short[samples];
		var samplesRead = manager.OutputAudioBuffer.Read(sampleBuffer);
		if (samplesRead < samples)
		{
			Debug.WriteLine($"AUDIO UNDERRUN! Only read {samplesRead} samples (wanted {samples} samples)");
			sampleBuffer[samplesRead..].Clear();
		}

		fixed (short* sampleBufferPtr = sampleBuffer)
		{
			_ = SDL_PutAudioStreamData(stream, (nint)sampleBufferPtr, sampleBuffer.Length * 2);
		}
	}

	private readonly object _resamplerLock = new();

	private readonly BlipBuffer _resampler;
	private int _lastL, _lastR;
	private short[] _resamplingBuffer = [];

	private nint _sdlAudioDeviceStream;
	private GCHandle _sdlUserData;

	private unsafe int OpenAudioDevice()
	{
		if (!SDL_GetAudioDeviceFormat(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, out var deviceAudioSpec, out _))
		{
			throw new($"Failed to obtain the default audio device format, SDL error: {SDL_GetError()}");
		}

		var wantedAudioSpec = default(SDL_AudioSpec);
		wantedAudioSpec.freq = deviceAudioSpec.freq; // try to use the device sample rate, so we can avoid a secondary resampling by SDL or whatever native api is used
		wantedAudioSpec.format = SDL_AUDIO_S16;
		wantedAudioSpec.channels = 2;

		var audioDeviceStream = SDL_OpenAudioDeviceStream(
			devid: SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK,
			spec: ref wantedAudioSpec,
			callback: &SDLAudioCallback,
			userdata: GCHandle.ToIntPtr(_sdlUserData)
		);

		if (audioDeviceStream == 0)
		{
			throw new($"Failed to open audio device, SDL error: {SDL_GetError()}");
		}

		_sdlAudioDeviceStream = audioDeviceStream;
		return wantedAudioSpec.freq;
	}

	public void DispatchAudio(ReadOnlySpan<short> samples)
	{
		uint resamplerTime = 0;
		for (var i = 0; i < samples.Length; i += 2)
		{
			int l = samples[i + 0];
			int r = samples[i + 1];
			_resampler.AddDelta(resamplerTime++, l - _lastL, r - _lastR);
			_lastL = l;
			_lastR = r;
		}

		_resampler.EndFrame(resamplerTime);
		var samplesAvail = _resampler.SamplesAvail * 2;
		if (samplesAvail > _resamplingBuffer.Length)
		{
			_resamplingBuffer = new short[samplesAvail];
		}

		var samplesRead = _resampler.ReadSamples(_resamplingBuffer);
		OutputAudioBuffer.Write(_resamplingBuffer.AsSpan()[..((int)samplesRead * 2)]);
	}

	public AudioManager(int inputAudioFrequency, bool unthrottled)
	{
		// unthrottled makes this class do nothing
		if (unthrottled)
		{
			return;
		}

		if (!SDL_Init(SDL_InitFlags.SDL_INIT_AUDIO))
		{
			throw new($"Could not init SDL audio! SDL error: {SDL_GetError()}");
		}

		try
		{
			_sdlUserData = GCHandle.Alloc(this, GCHandleType.Weak);
			var outputAudioFrequency = OpenAudioDevice();

			_resampler = new(BitOperations.RoundUpToPowerOf2((uint)(outputAudioFrequency * 20 / 1000)));
			_resampler.SetRates(inputAudioFrequency, outputAudioFrequency);
			_resampler.Clear();
			OutputAudioBuffer.Reset(100 * 2 * outputAudioFrequency * 2 / 1000, 0);

			SDL_ResumeAudioStreamDevice(_sdlAudioDeviceStream);
		}
		catch
		{
			Dispose();
			throw;
		}
	}

	public void Dispose()
	{
		_resampler?.Dispose();

		if (_sdlAudioDeviceStream != 0)
		{
			SDL_DestroyAudioStream(_sdlAudioDeviceStream);
		}

		if (_sdlUserData.IsAllocated)
		{
			_sdlUserData.Free();
		}

		SDL_QuitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO);
	}
}
