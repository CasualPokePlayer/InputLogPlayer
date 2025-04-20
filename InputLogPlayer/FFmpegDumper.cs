// Copyright (c) 2025 CasualPokePlayer & BizHawk team
// SPDX-License-Identifier: MPL-2.0 or MIT

// A lot of this is copied from BizHawk's NutMuxer, with various performance improvements added on

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using InputLogPlayer.Audio;

namespace InputLogPlayer;

internal sealed class FFmpegDumper : IDisposable
{
	private static readonly Lazy<bool> _isAvailable = new(() =>
	{
		var startInfo = new ProcessStartInfo("ffmpeg", "-version")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			StandardOutputEncoding = Encoding.UTF8,
		};

		using var process = new Process();
		process.StartInfo = startInfo;

		try
		{
			process.Start();
			if (!process.WaitForExit(5000))
			{
				process.Kill();
				return false;
			}
		}
		catch
		{
			return false;
		}

		var stdout = process.StandardOutput;
		while (stdout.ReadLine() is { } line)
		{
			if (line.Contains("ffmpeg version"))
			{
				return true;
			}
		}

		return false;
	});

	public static bool IsAvailable => _isAvailable.Value;

	// everything is resampled to 48KHz
	private const uint OUT_SAMPLE_RATE = 48000;
	// GB and GBA are both the exact same rate (~59.7275 FPS), so this can be hardcoded (for now)
	private const uint FPS_NUM = 262144;
	private const uint FPS_DEN = 4389;

	private readonly Process _ffmpegProcess;
	private readonly Stream _ffmpegStdin;
	private readonly BlipBuffer _resampler;
	private int _lastL, _lastR;
	private short[] _resamplingBuffer = [];
	private ulong _audioPts;

	public FFmpegDumper(string gm2Path, int audioFreq, int videoWidth, int videoHeight, bool dumpAv)
	{
		if (!dumpAv)
		{
			// dummy out this class if not dumping a/v
			return;
		}

		try
		{
			var mp4Path = Path.ChangeExtension(gm2Path, ".mp4");
			var args = $"-y -f nut -i - -vf scale=iw*4:ih*4 -crf 18 -sws_flags neighbor -pix_fmt yuv420p -b:a 384k -f mp4 \"{mp4Path}\"";
			var startInfo = new ProcessStartInfo("ffmpeg", args)
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardInput = true
			};

            _ffmpegProcess = new()
            {
                StartInfo = startInfo
            };
            _ffmpegProcess.Start();
			_ffmpegStdin = _ffmpegProcess.StandardInput.BaseStream;
			WriteMainHeader();
			WriteVideoHeader(videoWidth, videoHeight);
			WriteAudioHeader();

			_resampler = new BlipBuffer(1024);
			_resampler.SetRates(audioFreq, OUT_SAMPLE_RATE);
		}
		catch
		{
			_ffmpegProcess?.Kill();
			_ffmpegProcess?.Dispose();
			_ffmpegProcess = null;
			Dispose();
			throw;
		}
	}

	private enum NutStartCode : ulong
	{
		Main = 0x4E4D7A561F5F04AD,
		Stream = 0x4E5311405BF2F9DB,
		Syncpoint = 0x4E4BE4ADEECA4569,
	}

	private ref struct NutBuffer
	{
		// keep the buffer at page sizes
		// (shouldn't really matter if page size is actually different, this is mainly for slight allocation performance)
		private const uint PAGE_SIZE = 0x1000;
		private const uint PAGE_MASK = PAGE_SIZE - 1;
		private const uint MIN_BUFFER_SIZE = PAGE_SIZE;
		// not really a hard limitation for NUT, but rather one for dealing with Spans
		// nothing should be coming close to this anyways
		private const uint MAX_BUFFER_SIZE = int.MaxValue & ~PAGE_MASK;

		private unsafe void* _buffer;
		private uint _length;
		private uint _pos;

		public NutBuffer()
		{
			unsafe
			{
				_buffer = NativeMemory.Alloc(MIN_BUFFER_SIZE);
			}

			_length = MIN_BUFFER_SIZE;
		}

		public void OutputPacket(NutStartCode startCode, Stream output)
		{
			CheckDisposed();

			WriteChecksum();
			var bufferUsed = BufferUsedSpan();

			using var header = new NutBuffer();
			header.Write64((ulong)startCode);
			header.WriteVar((uint)bufferUsed.Length);
			if (bufferUsed.Length > 4096)
			{
				header.WriteChecksum();
			}

			output.Write(header.BufferUsedSpan());
			output.Write(bufferUsed);
			Dispose();
		}

		public void OutputFrame(ReadOnlySpan<byte> payload, ulong pts, ulong ptsIndex, Stream output)
		{
			CheckDisposed();

			// create syncpoint
			using var packet = new NutBuffer();
			packet.WriteVar(pts * 2 + ptsIndex); // global_key_pts
			packet.WriteVar(1); // back_ptr_div_16, this is wrong
			packet.OutputPacket(NutStartCode.Syncpoint, output);

			WriteByte(0); // frame_code

			// frame_flags = FLAG_CODED, so:
			var flags = 0u;
			flags |= 1 << 0; // FLAG_KEY
			if (payload.IsEmpty)
			{
				flags |= 1 << 1; // FLAG_EOR
			}

			flags |= 1 << 3; // FLAG_CODED_PTS
			flags |= 1 << 4; // FLAG_STREAM_ID
			flags |= 1 << 5; // FLAG_SIZE_MSB
			flags |= 1 << 6; // FLAG_CHECKSUM
			WriteVar(flags);
			WriteVar(ptsIndex); // stream_id
			WriteVar(pts + 256); // coded_pts = pts + 1 << msb_pts_shift
			WriteVar((uint)payload.Length); // data_size_msb

			WriteChecksum();
			var bufferUsed = BufferUsedSpan();
			output.Write(bufferUsed);
			output.Write(payload);

			Dispose();
		}

		public void WriteVar(ulong v)
		{
			CheckDisposed();

			Span<byte> b = stackalloc byte[10];
			var i = 0;
			b[i++] = (byte)(v & 0x7F);
			v /= 0x80;
			while (v > 0)
			{
				b[i++] = (byte)((v & 0x7F) | 0x80);
				v /= 0x80;
			}

			EnsureBuffer((uint)i);
			var bufferAvail = BufferAvailSpan();
			var j = 0;
			for (; i > 0; i--)
			{
				bufferAvail[j++] = b[i - 1];
			}

			_pos += (uint)j;
		}

		public void WriteBytes(ReadOnlySpan<byte> b)
		{
			CheckDisposed();
			WriteVar((uint)b.Length);
			EnsureBuffer((uint)b.Length);
			var bufferAvail = BufferAvailSpan();
			b.CopyTo(bufferAvail);
			_pos += (uint)b.Length;
		}

		private void WriteByte(byte b)
		{
			EnsureBuffer(sizeof(byte));
			var bufferAvail = BufferAvailSpan();
			bufferAvail[0] = b;
			_pos += sizeof(byte);
		}

		private void Write32(uint v)
		{
			EnsureBuffer(sizeof(uint));
			var bufferAvail = BufferAvailSpan();
			BinaryPrimitives.WriteUInt32BigEndian(bufferAvail, v);
			_pos += sizeof(uint);
		}

		private void Write64(ulong v)
		{
			EnsureBuffer(sizeof(ulong));
			var bufferAvail = BufferAvailSpan();
			BinaryPrimitives.WriteUInt64BigEndian(bufferAvail, v);
			_pos += sizeof(ulong);
		}

		private static readonly uint[] NutCrcTable =
		[
			0x00000000, 0x04C11DB7, 0x09823B6E, 0x0D4326D9,
			0x130476DC, 0x17C56B6B, 0x1A864DB2, 0x1E475005,
			0x2608EDB8, 0x22C9F00F, 0x2F8AD6D6, 0x2B4BCB61,
			0x350C9B64, 0x31CD86D3, 0x3C8EA00A, 0x384FBDBD,
		];

		private void WriteChecksum()
		{
			var bufferUsed = BufferUsedSpan();
			uint crc = 0;
			foreach (var b in bufferUsed)
			{
				crc ^= (uint)b << 24;
				crc = (crc << 4) ^ NutCrcTable[crc >> 28];
				crc = (crc << 4) ^ NutCrcTable[crc >> 28];
			}

			Write32(crc);
		}

		private readonly unsafe ReadOnlySpan<byte> BufferUsedSpan()
		{
			return new ReadOnlySpan<byte>(_buffer, (int)_pos);
		}

		private readonly unsafe Span<byte> BufferAvailSpan()
		{
			return new Span<byte>((byte*)_buffer + _pos, (int)(_length - _pos));
		}

		private unsafe void EnsureBuffer(uint spaceNeeded)
		{
			var neededLength = _pos + spaceNeeded;
			if (neededLength > MAX_BUFFER_SIZE)
			{
				throw new Exception("Exceeded maximum buffer size");
			}

			if (neededLength > _length)
			{
				_length = ((_length + spaceNeeded - 1) | PAGE_MASK) + 1;
				_buffer = NativeMemory.Realloc(_buffer, _length);
			}
		}

		private readonly unsafe void CheckDisposed()
		{
			ObjectDisposedException.ThrowIf(_buffer == null, typeof(NutBuffer));
		}

		public unsafe void Dispose()
		{
			NativeMemory.Free(_buffer);
			_buffer = null;
		}
	}

	// write out the main header
	private void WriteMainHeader()
	{
		// not part of the actual main header, just comes before such
		_ffmpegStdin.Write("nut/multimedia container\0"u8);

		using var packet = new NutBuffer();
		packet.WriteVar(3); // version
		packet.WriteVar(2); // stream_count
		packet.WriteVar(65536); // max_distance

		packet.WriteVar(2); // time_base_count
		// timebase is length of single frame, so reversed num + den is intentional
		packet.WriteVar(FPS_DEN); // time_base_num[0]
		packet.WriteVar(FPS_NUM); // time_base_den[0]
		// sound is resampled by us to 48KHz
		packet.WriteVar(1); // time_base_num[1]
		packet.WriteVar(OUT_SAMPLE_RATE); // time_base_den[1]

		// frame flag compression is ignored for simplicity
		for (var i = 0; i < 255; i++) // not 256 because entry 0x4E is skipped (as it would indicate a startcode)
		{
			packet.WriteVar(1 << 12); // tmp_flag = FLAG_CODED
			packet.WriteVar(0); // tmp_fields
		}

		// header compression ignored because it's not useful to us
		packet.WriteVar(0); // header_count_minus1

		// BROADCAST_MODE only useful for realtime transmission clock recovery
		packet.WriteVar(0); // main_flags

		packet.OutputPacket(NutStartCode.Main, _ffmpegStdin);
	}

	// write out the 0th stream header (video)
	private void WriteVideoHeader(int videoWidth, int videoHeight)
	{
		using var packet = new NutBuffer();
		packet.WriteVar(0); // stream_id
		packet.WriteVar(0); // stream_class = video
		packet.WriteBytes("BGRA"u8); // fourcc = "BGRA"
		packet.WriteVar(0); // time_base_id = 0
		packet.WriteVar(8); // msb_pts_shift
		packet.WriteVar(1); // max_pts_distance
		packet.WriteVar(0); // decode_delay
		packet.WriteVar(1); // stream_flags = FLAG_FIXED_FPS
		packet.WriteBytes([]); // codec_specific_data

		// stream_class = video
		packet.WriteVar((uint)videoWidth); // width
		packet.WriteVar((uint)videoHeight); // height
		packet.WriteVar(1); // sample_width
		packet.WriteVar(1); // sample_height
		packet.WriteVar(18); // colorspace_type = full range rec709 (avisynth's "PC.709")

		packet.OutputPacket(NutStartCode.Stream, _ffmpegStdin);
	}

	// write out the 1st stream header (audio)
	private void WriteAudioHeader()
	{
		using var packet = new NutBuffer();
		packet.WriteVar(1); // stream_id
		packet.WriteVar(1); // stream_class = audio
		packet.WriteBytes([0x01, 0x00, 0x00, 0x00]); // fourcc = 01 00 00 00
		packet.WriteVar(1); // time_base_id = 1
		packet.WriteVar(8); // msb_pts_shift
		packet.WriteVar(OUT_SAMPLE_RATE); // max_pts_distance
		packet.WriteVar(0); // decode_delay
		packet.WriteVar(0); // stream_flags = none; no FIXED_FPS because we aren't guaranteeing same-size audio chunks
		packet.WriteBytes([]); // codec_specific_data

		// stream_class = audio
		packet.WriteVar(OUT_SAMPLE_RATE); // samplerate_num
		packet.WriteVar(1); // samplerate_den
		packet.WriteVar(2); // channel_count

		packet.OutputPacket(NutStartCode.Stream, _ffmpegStdin);
	}

	public void WriteVideoFrame(ReadOnlySpan<uint> videoBuffer)
	{
		using var frame = new NutBuffer();
		var payload = MemoryMarshal.AsBytes(videoBuffer);
		// video PTS is calculated off audio PTS, as there isn't a guarantee video frames will arrive consistently
		var videoPts = _audioPts * FPS_NUM / (OUT_SAMPLE_RATE * FPS_DEN);
		frame.OutputFrame(payload, videoPts, 0, _ffmpegStdin);
	}

	public void WriteAudioFrame(ReadOnlySpan<short> audioBuffer)
	{
		using var frame = new NutBuffer();

		if (audioBuffer.IsEmpty)
		{
			frame.OutputFrame([], _audioPts, 1, _ffmpegStdin);
			return;
		}

		uint resamplerTime = 0;
		for (var i = 0; i < audioBuffer.Length; i += 2)
		{
			int l = audioBuffer[i + 0];
			int r = audioBuffer[i + 1];
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
		var payload = MemoryMarshal.AsBytes(_resamplingBuffer.AsSpan()[..((int)samplesRead * 2)]);
		frame.OutputFrame(payload, _audioPts, 1, _ffmpegStdin);
		_audioPts += samplesRead;
	}

	public void Dispose()
	{
		if (_ffmpegProcess != null)
		{
			WriteVideoFrame([]);
			WriteAudioFrame([]);
			_ffmpegStdin.Flush();
			// can't really gracefully shutdown ffmpeg here
			// hope it shuts down gracefully itself...
			_ffmpegProcess.Dispose();
		}

		_resampler?.Dispose();
	}
}
