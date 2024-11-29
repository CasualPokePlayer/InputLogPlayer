// Copyright (c) 2024 CasualPokePlayer
// SPDX-License-Identifier: MPL-2.0

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using static SDL2.SDL;

namespace InputLogPlayer.Audio;

// NOTE: Previously I tried a threading approach which offloaded resampling to a separate thread...
// That approach was very complicated and audio did not work at all for reasons I can't comprehend (edit: blipbuf managed impl was bugged, fixed now, maybe can reintroduce?)
// Thus, resampling is done on the emu thread, and we let SDL handle obtaining samples in its audio callback (called on a separate thread anyways)
public sealed class AudioManager : IDisposable
{
	private readonly AudioRingBuffer OutputAudioBuffer = new();

	[UnmanagedCallersOnly(CallConvs = [ typeof(CallConvCdecl) ])]
	private static unsafe void SDLAudioCallback(nint userdata, nint stream, int len)
	{
		var manager = (AudioManager)GCHandle.FromIntPtr(userdata).Target!;
		var samples = len / 2;
		var samplesRead = manager.OutputAudioBuffer.Read(new((void*)stream, samples));
		if (samplesRead < samples)
		{
			Debug.WriteLine($"AUDIO UNDERRUN! Only read {samplesRead} samples (wanted {samples} samples)");
			new Span<short>((void*)(stream + samplesRead * 2), samples - samplesRead).Clear();
		}
	}

	private readonly BlipBuffer _resampler;
	private int _lastL, _lastR;
	private short[] _resamplingBuffer = [];

	private uint _sdlAudioDeviceId;
	private GCHandle _sdlUserData;

	private static int GetDefaultDeviceSampleRate()
	{
		const int FALLBACK_FREQ = 48000;
		return SDL_GetDefaultAudioInfo(out _, out var spec, iscapture: 0) == 0 ? spec.freq : FALLBACK_FREQ;
	}

	private int OpenAudioDevice()
	{
		var wantedSdlAudioSpec = default(SDL_AudioSpec);
		wantedSdlAudioSpec.freq = GetDefaultDeviceSampleRate(); // try to use the device sample rate, so we can avoid a secondary resampling by SDL or whatever native api is used
		wantedSdlAudioSpec.format = AUDIO_S16SYS;
		wantedSdlAudioSpec.channels = 2;
		wantedSdlAudioSpec.samples = 512; // we'll let this change to however SDL best wants it
		wantedSdlAudioSpec.userdata = GCHandle.ToIntPtr(_sdlUserData);

		unsafe
		{
			wantedSdlAudioSpec.callback = &SDLAudioCallback;
		}

		var deviceId = SDL_OpenAudioDevice(
			device: null,
			iscapture: 0,
			desired: ref wantedSdlAudioSpec,
			obtained: out var obtainedAudioSpec,
			allowed_changes: (int)(SDL_AUDIO_ALLOW_SAMPLES_CHANGE | SDL_AUDIO_ALLOW_FREQUENCY_CHANGE)
		);

		if (deviceId == 0)
		{
			throw new($"Failed to open audio device, SDL error: {SDL_GetError()}");
		}

		_sdlAudioDeviceId = deviceId;
		return obtainedAudioSpec.freq;
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

		if (SDL_Init(SDL_INIT_AUDIO) != 0)
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

			SDL_PauseAudioDevice(_sdlAudioDeviceId, pause_on: 0);
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

		if (_sdlAudioDeviceId != 0)
		{
			SDL_CloseAudioDevice(_sdlAudioDeviceId);
		}

		if (_sdlUserData.IsAllocated)
		{
			_sdlUserData.Free();
		}

		SDL_QuitSubSystem(SDL_INIT_AUDIO);
	}
}
