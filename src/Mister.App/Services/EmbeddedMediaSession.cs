using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Mister.App.Services;

/// <summary>
/// Decodes local audio and video with the bundled FFmpeg libraries.
/// Ogg Vorbis and Bink video are decoded in-process.
/// </summary>
internal sealed unsafe class EmbeddedMediaSession : IDisposable
{
    public const int OutputSampleRate = 48000;
    private const int OutputChannels = 2;
    private const int BytesPerSample = 2;
    private const int BytesPerSecond = OutputSampleRate * OutputChannels * BytesPerSample;
    private const int AudioBufferMilliseconds = 100;
    private const int AudioBufferCount = 8;
    private const int AudioLowWaterBytes = BytesPerSecond / 5;
    private const int MaxHeldPictures = 2;
    private const uint WaveMapper = unchecked((uint)-1);
    private const uint TimeMilliseconds = 1;
    private const uint TimeSamples = 2;
    private const uint TimeBytes = 4;
    private const uint WhdrDone = 0x00000001;
    private const int ErrorAgain = -11;

    private static readonly object NativeGate = new();
    private static bool nativeReady;

    private readonly object gate = new();
    private readonly ManualResetEventSlim wake = new(false);
    private readonly Queue<VideoPicture> pictures = new();
    private readonly PcmQueue audio = new();
    private readonly Thread thread;

    private AVFormatContext* format;
    private AVCodecContext* videoCodec;
    private AVCodecContext* audioCodec;
    private SwsContext* scaler;
    private SwrContext* resampler;
    private AVFrame* decodedFrame;
    private AVFrame* bgraFrame;
    private AVPacket* packet;
    private AVPacket* stashedPacket;
    private AVStream* videoStream;
    private AVStream* audioStream;
    private int videoIndex = -1;
    private int audioIndex = -1;
    private int scalerFormat = -1;
    private int scalerWidth;
    private int scalerHeight;
    private bool inputEnded;
    private bool hasStash;
    private bool stopRequested;
    private bool playing;
    private bool loop;
    private bool endedRaised;
    private bool disposed;
    private TimeSpan audioOrigin;
    private TimeSpan videoOrigin;
    private readonly Stopwatch videoClock = new();
    private int volume = 80;

    private IntPtr waveOut;
    private readonly IntPtr[] waveHeaders = new IntPtr[AudioBufferCount];
    private readonly IntPtr[] waveBuffers = new IntPtr[AudioBufferCount];
    private readonly bool[] waveBusy = new bool[AudioBufferCount];
    private readonly int waveBufferBytes =
        BytesPerSecond * AudioBufferMilliseconds / 1000;

    private EmbeddedMediaSession(string path)
    {
        EnsureNative();
        try
        {
            Open(path);
        }
        catch
        {
            FreeNative();
            throw;
        }

        thread = new Thread(PlaybackLoop)
        {
            IsBackground = true,
            Name = "Technika Tool media decoder"
        };
        thread.Start();
    }

    public bool HasVideo => videoCodec is not null;

    public bool HasAudio => audioCodec is not null;

    public TimeSpan Duration { get; private set; }

    public bool Loop
    {
        get
        {
            lock (gate) return loop;
        }
        set
        {
            lock (gate) loop = value;
            wake.Set();
        }
    }

    public int Volume
    {
        get
        {
            lock (gate) return volume;
        }
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            IntPtr handle;
            lock (gate)
            {
                volume = clamped;
                handle = waveOut;
            }

            if (handle != IntPtr.Zero)
            {
                uint level = (uint)(clamped * 0xFFFF / 100);
                _ = waveOutSetVolume(handle, level | (level << 16));
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            IntPtr handle;
            TimeSpan origin;
            lock (gate)
            {
                if (disposed) return TimeSpan.Zero;
                handle = waveOut;
                if (handle == IntPtr.Zero)
                {
                    TimeSpan elapsed = videoClock.IsRunning
                        ? videoClock.Elapsed
                        : TimeSpan.Zero;
                    return Clamp(videoOrigin + elapsed);
                }

                origin = audioOrigin;
            }

            var time = new MmtTime { Type = TimeSamples };
            if (waveOutGetPosition(
                    handle,
                    ref time,
                    (uint)Marshal.SizeOf<MmtTime>()) != 0)
            {
                return Clamp(origin);
            }

            double seconds = time.Type switch
            {
                TimeMilliseconds => time.Value / 1000d,
                TimeBytes => time.Value / (double)BytesPerSecond,
                _ => time.Value / (double)OutputSampleRate
            };
            return Clamp(origin + TimeSpan.FromSeconds(Math.Max(0, seconds)));
        }
    }

    public event Action<byte[], int, int, int>? FrameReady;

    public event Action? PlaybackEnded;

    public event Action<string>? Failed;

    public static EmbeddedMediaSession OpenFile(string path) => new(path);

    public void Play()
    {
        lock (gate)
        {
            if (disposed) return;
            if (endedRaised) pendingSeek = TimeSpan.Zero;
            playing = true;
            endedRaised = false;
            if (waveOut == IntPtr.Zero) videoClock.Start();
        }

        ResumeWave();
        wake.Set();
    }

    public void Pause()
    {
        lock (gate)
        {
            if (disposed) return;
            playing = false;
            if (videoClock.IsRunning) videoClock.Stop();
        }

        IntPtr handle = WaveHandle();
        if (handle != IntPtr.Zero) _ = waveOutPause(handle);
        wake.Set();
    }

    public void Stop()
    {
        Seek(TimeSpan.Zero);
        Pause();
    }

    public void Seek(TimeSpan position)
    {
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        lock (gate)
        {
            if (disposed) return;
            if (Duration > TimeSpan.Zero && position > Duration) position = Duration;
            pendingSeek = position;
            endedRaised = false;
        }

        wake.Set();
    }

    private TimeSpan? pendingSeek;

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            stopRequested = true;
        }

        wake.Set();
        if (thread.IsAlive) thread.Join();
        CloseWave();
        FreeNative();
        wake.Dispose();
    }

    private void Open(string path)
    {
        AVFormatContext* input = null;
        int error = ffmpeg.avformat_open_input(&input, path, null, null);
        if (error < 0 || input is null)
        {
            throw new InvalidDataException(
                "The media file could not be opened. "
                + Describe(error));
        }

        format = input;
        error = ffmpeg.avformat_find_stream_info(format, null);
        if (error < 0)
        {
            throw new InvalidDataException(
                "The media streams could not be read. " + Describe(error));
        }

        if (format->duration > 0)
        {
            Duration = TimeSpan.FromTicks(
                format->duration * TimeSpan.TicksPerSecond / ffmpeg.AV_TIME_BASE);
        }

        decodedFrame = ffmpeg.av_frame_alloc();
        packet = ffmpeg.av_packet_alloc();
        stashedPacket = ffmpeg.av_packet_alloc();
        if (decodedFrame is null || packet is null || stashedPacket is null)
        {
            throw new InvalidDataException("The media decoder could not allocate buffers.");
        }

        OpenVideo();
        OpenAudio();
        if (videoCodec is null && audioCodec is null)
        {
            throw new InvalidDataException(
                "This file does not contain a supported audio or video stream.");
        }

        if (audioCodec is not null) OpenWave();
    }

    private void OpenVideo()
    {
        AVCodec* decoder = null;
        int index = ffmpeg.av_find_best_stream(
            format,
            AVMediaType.AVMEDIA_TYPE_VIDEO,
            -1,
            -1,
            &decoder,
            0);
        if (index < 0 || decoder is null) return;
        AVStream* stream = format->streams[index];
        AVCodecContext* context = ffmpeg.avcodec_alloc_context3(decoder);
        if (context is null) return;
        if (ffmpeg.avcodec_parameters_to_context(context, stream->codecpar) < 0
            || OpenCodec(context, decoder) < 0)
        {
            ffmpeg.avcodec_free_context(&context);
            return;
        }

        videoIndex = index;
        videoStream = stream;
        videoCodec = context;
        bgraFrame = ffmpeg.av_frame_alloc();
    }

    private void OpenAudio()
    {
        AVCodec* decoder = null;
        int index = ffmpeg.av_find_best_stream(
            format,
            AVMediaType.AVMEDIA_TYPE_AUDIO,
            -1,
            videoIndex,
            &decoder,
            0);
        if (index < 0 || decoder is null) return;
        AVStream* stream = format->streams[index];
        AVCodecContext* context = ffmpeg.avcodec_alloc_context3(decoder);
        if (context is null) return;
        if (ffmpeg.avcodec_parameters_to_context(context, stream->codecpar) < 0
            || OpenCodec(context, decoder) < 0)
        {
            ffmpeg.avcodec_free_context(&context);
            return;
        }

        AVChannelLayout outputLayout;
        ffmpeg.av_channel_layout_default(&outputLayout, OutputChannels);
        SwrContext* converter = null;
        int configured = ffmpeg.swr_alloc_set_opts2(
            &converter,
            &outputLayout,
            AVSampleFormat.AV_SAMPLE_FMT_S16,
            OutputSampleRate,
            &context->ch_layout,
            (AVSampleFormat)context->sample_fmt,
            context->sample_rate,
            0,
            null);
        ffmpeg.av_channel_layout_uninit(&outputLayout);
        if (configured < 0 || converter is null || ffmpeg.swr_init(converter) < 0)
        {
            if (converter is not null) ffmpeg.swr_free(&converter);
            ffmpeg.avcodec_free_context(&context);
            return;
        }

        audioIndex = index;
        audioStream = stream;
        audioCodec = context;
        resampler = converter;
    }

    private static int OpenCodec(AVCodecContext* context, AVCodec* decoder)
    {
        return ffmpeg.avcodec_open2(context, decoder, null);
    }

    private void PlaybackLoop()
    {
        try
        {
            while (true)
            {
                TimeSpan? seek;
                bool run;
                bool shouldLoop;
                lock (gate)
                {
                    if (stopRequested) return;
                    seek = pendingSeek;
                    pendingSeek = null;
                    run = playing;
                    shouldLoop = loop;
                }

                if (seek is not null) ApplySeek(seek.Value);
                if (!run)
                {
                    wake.Wait(100);
                    wake.Reset();
                    continue;
                }

                ResumeWave();
                PumpAudio();
                if (!inputEnded && NeedsMorePackets())
                {
                    ReadPacket();
                }

                PublishDuePicture();
                if (inputEnded
                    && audio.Count == 0
                    && pictures.Count == 0
                    && !WaveBusy())
                {
                    if (shouldLoop)
                    {
                        ApplySeek(TimeSpan.Zero);
                        continue;
                    }

                    RaiseEnded();
                    lock (gate) playing = false;
                    continue;
                }

                if (pictures.Count > 0 || audio.Count >= AudioLowWaterBytes || inputEnded)
                {
                    Thread.Sleep(8);
                }
            }
        }
        catch (Exception exception) when (exception is not ThreadInterruptedException)
        {
            try
            {
                Failed?.Invoke(
                    "This media could not be decoded. " + exception.Message);
            }
            catch (Exception)
            {
                // The view may already be gone.
            }
        }
    }

    private bool NeedsMorePackets()
    {
        bool audioHungry = audioCodec is not null && audio.Count < AudioLowWaterBytes;
        bool videoHungry = videoCodec is not null && pictures.Count == 0;
        if (audioCodec is null) return videoHungry;
        if (videoCodec is null) return audioHungry;
        return audioHungry || videoHungry;
    }

    private void ReadPacket()
    {
        if (hasStash)
        {
            hasStash = false;
            DispatchPacket(stashedPacket);
            ffmpeg.av_packet_unref(stashedPacket);
            return;
        }

        int error = ffmpeg.av_read_frame(format, packet);
        if (error == ffmpeg.AVERROR_EOF)
        {
            FlushDecoder(videoCodec, isVideo: true);
            FlushDecoder(audioCodec, isVideo: false);
            inputEnded = true;
            return;
        }

        if (error < 0)
        {
            inputEnded = true;
            return;
        }

        try
        {
            if (packet->stream_index == videoIndex && pictures.Count >= MaxHeldPictures)
            {
                ffmpeg.av_packet_move_ref(stashedPacket, packet);
                hasStash = true;
                return;
            }

            DispatchPacket(packet);
        }
        finally
        {
            ffmpeg.av_packet_unref(packet);
        }
    }

    private void DispatchPacket(AVPacket* source)
    {
        if (source->stream_index == videoIndex && videoCodec is not null)
        {
            DecodeVideo(source);
        }
        else if (source->stream_index == audioIndex && audioCodec is not null)
        {
            DecodeAudio(source);
        }
    }

    private void FlushDecoder(AVCodecContext* context, bool isVideo)
    {
        if (context is null) return;
        _ = ffmpeg.avcodec_send_packet(context, null);
        if (isVideo) DrainVideo();
        else DrainAudio();
    }

    private void DecodeVideo(AVPacket* source)
    {
        int error = ffmpeg.avcodec_send_packet(videoCodec, source);
        if (error < 0 && error != ErrorAgain) return;
        DrainVideo();
    }

    private void DrainVideo()
    {
        while (true)
        {
            int error = ffmpeg.avcodec_receive_frame(videoCodec, decodedFrame);
            if (error < 0) return;
            try
            {
                HoldVideoFrame(decodedFrame);
            }
            finally
            {
                ffmpeg.av_frame_unref(decodedFrame);
            }
        }
    }

    private void HoldVideoFrame(AVFrame* frame)
    {
        int width = frame->width;
        int height = frame->height;
        if (width <= 0 || height <= 0) return;
        if (scaler is null
            || scalerFormat != frame->format
            || scalerWidth != width
            || scalerHeight != height)
        {
            if (scaler is not null) ffmpeg.sws_freeContext(scaler);
            scaler = ffmpeg.sws_getContext(
                width,
                height,
                (AVPixelFormat)frame->format,
                width,
                height,
                AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_BILINEAR,
                null,
                null,
                null);
            scalerFormat = frame->format;
            scalerWidth = width;
            scalerHeight = height;
            if (bgraFrame is not null)
            {
                FreeFrame(ref bgraFrame);
                bgraFrame = ffmpeg.av_frame_alloc();
            }
        }

        if (scaler is null || bgraFrame is null) return;
        if (bgraFrame->width != width || bgraFrame->height != height)
        {
            ffmpeg.av_frame_unref(bgraFrame);
            bgraFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
            bgraFrame->width = width;
            bgraFrame->height = height;
            if (ffmpeg.av_frame_get_buffer(bgraFrame, 32) < 0) return;
        }

        if (ffmpeg.sws_scale_frame(scaler, bgraFrame, frame) < 0) return;
        int stride = bgraFrame->linesize[0];
        if (stride <= 0) return;
        int size = stride * height;
        var pixels = new byte[size];
        Marshal.Copy((IntPtr)bgraFrame->data[0], pixels, 0, size);
        pictures.Enqueue(new VideoPicture(
            Seconds(frame, videoStream),
            pixels,
            width,
            height,
            stride));
    }

    private void DecodeAudio(AVPacket* source)
    {
        int error = ffmpeg.avcodec_send_packet(audioCodec, source);
        if (error < 0 && error != ErrorAgain) return;
        DrainAudio();
    }

    private void DrainAudio()
    {
        while (true)
        {
            int error = ffmpeg.avcodec_receive_frame(audioCodec, decodedFrame);
            if (error < 0) return;
            try
            {
                ConvertAudio(decodedFrame);
            }
            finally
            {
                ffmpeg.av_frame_unref(decodedFrame);
            }
        }
    }

    private void ConvertAudio(AVFrame* frame)
    {
        if (resampler is null || frame->nb_samples <= 0) return;
        int capacity = (int)Math.Ceiling(
            frame->nb_samples * (OutputSampleRate / (double)Math.Max(1, frame->sample_rate)))
            + 32;
        int bytes = capacity * OutputChannels * BytesPerSample;
        var converted = new byte[bytes];
        fixed (byte* destination = converted)
        {
            byte* plane = destination;
            int samples = ffmpeg.swr_convert(
                resampler,
                &plane,
                capacity,
                frame->extended_data,
                frame->nb_samples);
            if (samples <= 0) return;
            audio.Write(converted.AsSpan(0, samples * OutputChannels * BytesPerSample));
        }
    }

    private void PublishDuePicture()
    {
        if (pictures.Count == 0) return;
        double now = Position.TotalSeconds;
        VideoPicture picture = pictures.Peek();
        if (picture.Seconds > now + 0.04) return;
        pictures.Dequeue();
        if (picture.Seconds + 0.75 < now && pictures.Count > 0) return;
        FrameReady?.Invoke(picture.Pixels, picture.Width, picture.Height, picture.Stride);
    }

    private void ApplySeek(TimeSpan target)
    {
        long timestamp = (long)(target.TotalSeconds * ffmpeg.AV_TIME_BASE);
        _ = ffmpeg.av_seek_frame(
            format,
            -1,
            timestamp,
            ffmpeg.AVSEEK_FLAG_BACKWARD);
        if (videoCodec is not null) ffmpeg.avcodec_flush_buffers(videoCodec);
        if (audioCodec is not null) ffmpeg.avcodec_flush_buffers(audioCodec);
        if (resampler is not null)
        {
            ffmpeg.swr_close(resampler);
            _ = ffmpeg.swr_init(resampler);
        }

        if (hasStash)
        {
            ffmpeg.av_packet_unref(stashedPacket);
            hasStash = false;
        }

        pictures.Clear();
        audio.Clear();
        inputEnded = false;
        IntPtr handle = WaveHandle();
        if (handle != IntPtr.Zero)
        {
            _ = waveOutReset(handle);
            for (int index = 0; index < waveBusy.Length; index++) waveBusy[index] = false;
        }

        lock (gate)
        {
            audioOrigin = target;
            videoOrigin = target;
            videoClock.Restart();
            if (!playing || waveOut != IntPtr.Zero) videoClock.Stop();
        }
    }

    private void RaiseEnded()
    {
        bool raise;
        lock (gate)
        {
            raise = !endedRaised;
            endedRaised = true;
        }

        if (raise) PlaybackEnded?.Invoke();
    }

    private static double Seconds(AVFrame* frame, AVStream* stream)
    {
        long pts = frame->best_effort_timestamp;
        if (pts == ffmpeg.AV_NOPTS_VALUE) pts = frame->pts;
        if (pts == ffmpeg.AV_NOPTS_VALUE || stream is null) return 0;
        return pts * ffmpeg.av_q2d(stream->time_base);
    }

    private TimeSpan Clamp(TimeSpan value)
    {
        if (value < TimeSpan.Zero) return TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && value > Duration) return Duration;
        return value;
    }

    private void OpenWave()
    {
        var format = new WaveFormatEx
        {
            FormatTag = 1,
            Channels = OutputChannels,
            SamplesPerSec = OutputSampleRate,
            BitsPerSample = 16,
            BlockAlign = OutputChannels * BytesPerSample,
            Size = 0
        };
        format.AvgBytesPerSec = (uint)(format.SamplesPerSec * format.BlockAlign);
        IntPtr handle;
        if (waveOutOpen(
                out handle,
                WaveMapper,
                ref format,
                IntPtr.Zero,
                IntPtr.Zero,
                0) != 0)
        {
            return;
        }

        int headerSize = Marshal.SizeOf<WaveHdr>();
        for (int index = 0; index < AudioBufferCount; index++)
        {
            IntPtr data = Marshal.AllocHGlobal(waveBufferBytes);
            IntPtr header = Marshal.AllocHGlobal(headerSize);
            var waveHeader = new WaveHdr
            {
                Data = data,
                BufferLength = (uint)waveBufferBytes,
                Flags = 0
            };
            Marshal.StructureToPtr(waveHeader, header, false);
            if (waveOutPrepareHeader(handle, header, (uint)headerSize) != 0)
            {
                Marshal.FreeHGlobal(header);
                Marshal.FreeHGlobal(data);
                continue;
            }

            waveHeaders[index] = header;
            waveBuffers[index] = data;
        }

        lock (gate) waveOut = handle;
        Volume = volume;
    }

    private void PumpAudio()
    {
        IntPtr handle = WaveHandle();
        if (handle == IntPtr.Zero) return;
        int headerSize = Marshal.SizeOf<WaveHdr>();
        for (int index = 0; index < AudioBufferCount; index++)
        {
            if (waveHeaders[index] == IntPtr.Zero) continue;
            var header = Marshal.PtrToStructure<WaveHdr>(waveHeaders[index]);
            if (waveBusy[index])
            {
                if ((header.Flags & WhdrDone) == 0) continue;
                waveBusy[index] = false;
            }

            int filled = audio.Read(waveBuffers[index], waveBufferBytes);
            if (filled <= 0) continue;
            header.BufferLength = (uint)filled;
            header.Flags &= ~WhdrDone;
            Marshal.StructureToPtr(header, waveHeaders[index], false);
            if (waveOutWrite(handle, waveHeaders[index], (uint)headerSize) == 0)
            {
                waveBusy[index] = true;
            }
        }
    }

    private bool WaveBusy()
    {
        for (int index = 0; index < waveBusy.Length; index++)
        {
            if (waveBusy[index]) return true;
        }

        return false;
    }

    private void ResumeWave()
    {
        IntPtr handle = WaveHandle();
        if (handle != IntPtr.Zero) _ = waveOutRestart(handle);
    }

    private IntPtr WaveHandle()
    {
        lock (gate) return waveOut;
    }

    private void CloseWave()
    {
        IntPtr handle;
        lock (gate)
        {
            handle = waveOut;
            waveOut = IntPtr.Zero;
        }

        if (handle == IntPtr.Zero) return;
        _ = waveOutReset(handle);
        int headerSize = Marshal.SizeOf<WaveHdr>();
        for (int index = 0; index < AudioBufferCount; index++)
        {
            if (waveHeaders[index] != IntPtr.Zero)
            {
                _ = waveOutUnprepareHeader(handle, waveHeaders[index], (uint)headerSize);
                Marshal.FreeHGlobal(waveHeaders[index]);
                waveHeaders[index] = IntPtr.Zero;
            }

            if (waveBuffers[index] != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(waveBuffers[index]);
                waveBuffers[index] = IntPtr.Zero;
            }
        }

        _ = waveOutClose(handle);
    }

    private void FreeNative()
    {
        if (scaler is not null)
        {
            ffmpeg.sws_freeContext(scaler);
            scaler = null;
        }

        FreeResampler(ref resampler);
        FreeFrame(ref bgraFrame);
        FreeFrame(ref decodedFrame);
        FreePacket(ref packet);
        FreePacket(ref stashedPacket);
        FreeCodec(ref videoCodec);
        FreeCodec(ref audioCodec);
        FreeFormat(ref format);
    }

    private static void FreeFrame(ref AVFrame* frame)
    {
        AVFrame* local = frame;
        if (local is null) return;
        ffmpeg.av_frame_free(&local);
        frame = null;
    }

    private static void FreePacket(ref AVPacket* packet)
    {
        AVPacket* local = packet;
        if (local is null) return;
        ffmpeg.av_packet_free(&local);
        packet = null;
    }

    private static void FreeCodec(ref AVCodecContext* context)
    {
        AVCodecContext* local = context;
        if (local is null) return;
        ffmpeg.avcodec_free_context(&local);
        context = null;
    }

    private static void FreeResampler(ref SwrContext* context)
    {
        SwrContext* local = context;
        if (local is null) return;
        ffmpeg.swr_free(&local);
        context = null;
    }

    private static void FreeFormat(ref AVFormatContext* context)
    {
        AVFormatContext* local = context;
        if (local is null) return;
        ffmpeg.avformat_close_input(&local);
        context = null;
    }

    private static void EnsureNative()
    {
        lock (NativeGate)
        {
            if (nativeReady) return;
            string directory = AppContext.BaseDirectory;
            _ = SetDllDirectory(directory);
            ffmpeg.RootPath = directory;
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            nativeReady = true;
        }
    }

    private static string Describe(int error)
    {
        byte* buffer = stackalloc byte[256];
        ffmpeg.av_strerror(error, buffer, 256);
        return Marshal.PtrToStringUTF8((IntPtr)buffer) ?? $"FFmpeg error {error}";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);

    [DllImport("winmm.dll")]
    private static extern uint waveOutOpen(
        out IntPtr handle,
        uint deviceId,
        ref WaveFormatEx format,
        IntPtr callback,
        IntPtr instance,
        uint flags);

    [DllImport("winmm.dll")]
    private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    private static extern uint waveOutReset(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern uint waveOutPause(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern uint waveOutRestart(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern uint waveOutClose(IntPtr handle);

    [DllImport("winmm.dll")]
    private static extern uint waveOutSetVolume(IntPtr handle, uint volume);

    [DllImport("winmm.dll")]
    private static extern uint waveOutGetPosition(IntPtr handle, ref MmtTime time, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MmtTime
    {
        public uint Type;
        public uint Value;
        public uint Reserved;
    }

    private sealed record VideoPicture(
        double Seconds,
        byte[] Pixels,
        int Width,
        int Height,
        int Stride);

    private sealed class PcmQueue
    {
        private byte[] buffer = new byte[256 * 1024];
        private int offset;
        private int count;

        public int Count => count;

        public void Write(ReadOnlySpan<byte> source)
        {
            if (source.IsEmpty) return;
            Compact();
            if (offset + count + source.Length > buffer.Length)
            {
                int needed = count + source.Length;
                int size = buffer.Length;
                while (size < needed) size *= 2;
                Array.Resize(ref buffer, size);
            }

            source.CopyTo(buffer.AsSpan(offset + count));
            count += source.Length;
        }

        public int Read(IntPtr destination, int length)
        {
            int taken = Math.Min(length, count);
            if (taken <= 0) return 0;
            Marshal.Copy(buffer, offset, destination, taken);
            offset += taken;
            count -= taken;
            return taken;
        }

        public void Clear()
        {
            offset = 0;
            count = 0;
        }

        private void Compact()
        {
            if (offset == 0 || count == 0)
            {
                if (count == 0) offset = 0;
                return;
            }

            Buffer.BlockCopy(buffer, offset, buffer, 0, count);
            offset = 0;
        }
    }
}
