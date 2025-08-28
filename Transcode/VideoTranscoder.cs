unsafe class VideoTranscoder : AVTranscoder
{
    public VideoDecoder     Decoder     = null!;
    public VideoEncoder     Encoder     = null!;
    public VideoFrame       Frame       = new();
    public VideoFrame       SWFrame     = new();
    public VideoStream      VideoStream = null!;

    HWFramesContext?        hwFrames;
    HWDeviceContext         hwDevice    = null!;
    AVPixelFormat           hwPixelFormat;
    bool                    hwDownload; // hw decoding > sw encoding (todo: hw transfer from hw -> hw)

    const string T = "V";

    VideoTranscoder(TranscoderSample ctx, VideoStream stream) : base(ctx, stream, Rescale(ctx.StartTimeMcs, TIME_BASE_Q, stream.Timebase)) { VideoStream = stream; }

    public static VideoTranscoder? Create(TranscoderSample ctx, VideoStream stream)
    {
        VideoTranscoder transcoder = new(ctx, stream);
        
        if (!transcoder.SetupDecoding())
        {
            transcoder.Dispose();
            return null;
        }

        return transcoder;
    }

    bool SetupDecoding()
    {
        VideoDecoderSpec? spec = CodecSpec.FindVideoDecoder(Stream.CodecId); // TODO: HW decoding priority based on hw device? (at least for AV1)
        //VideoDecoderSpec? spec = CodecSpec.FindHWVideoDecoders(Stream.CodecId)[0];

        if (spec == null)
        {
            WriteLine($"[#{Stream.Index} - {Stream.CodecId}] Could not find decoder spec");
            return false;
        }

        if (Ctx.Options.HWDevice != AVHWDeviceType.None)
        {
            hwDevice        = new(Ctx.Options.HWDevice, null);
            hwPixelFormat   = HWDeviceSpec.FindHWPixelFormat(Ctx.Options.HWDevice);
            Decoder         = new(spec, VideoStream, GetHWFormat)
            {
                HWDeviceContext = hwDevice,
                HWExtraFrames = spec.CodecId == AVCodecID.Av1 ? 20 : 0, // TBR: FFmpeg bug?
            };

            if (spec.CodecId == AVCodecID.H264 && VideoStream.CodecProfile.Profile == 66 /*Baseline*/) // TBR: if should allow it generally
                Decoder.HWAccelFlags = HWAccelFlags.AllowProfileMismatch;
        }
        else
            Decoder = new(spec, VideoStream) { Threads = 2 };

        //Decoder.ApplyCropping = false; // TBR: what the encoder needs?

        FFmpegResult ret;
        if ((ret = Decoder.Open(Ctx.Options.VideoDecoderOptions)).Failed)
        {
            WriteLine($"[#{Stream.Index} - {Stream.CodecId}] Could not open the decoder ({ret})");
            return false; 
        }

        return true;
    }

    AVPixelFormat GetHWFormat(AVCodecContext* s, AVPixelFormat* fmt)
    {
        List<AVPixelFormat> availablePixelFormats = ArrayUtils.GetPixelFormats(fmt); // for performance should check one by one for match

        if (!availablePixelFormats.Contains(hwPixelFormat))
        {
            var pixFmt = Decoder.GetFormatDefault(fmt);
            WriteLine($"[#{T}{Stream.Index}] HW Decoding with {hwPixelFormat} failed. Falling back to SW Decoding with {pixFmt}.");
            hwDevice?.Dispose();
            hwFrames?.Dispose();
            return pixFmt;
        }
        
        if (hwFrames == null)
        {
            hwFrames = new(Decoder, hwDevice, hwPixelFormat);
            hwFrames.InitFrames().ThrowOnFailure();
        }

        Decoder.HWFramesContext = hwFrames;

        return hwPixelFormat;
    }

    public override void MuxerBeforeWriteHeader()
    {
        StreamMux = new VideoStreamMux(Muxer, Encoder);
        //videoStreamMux.Metadata = videoStream.Metadata;

        // TBR: AVI supports hevc only if you pass the codec tag (codec id is not enough) | That will cause mpegts to try to mux vp9 as private data and possible others
        if (!Muxer.MuxerSpec.Supports(Encoder.CodecSpec.CodecId))
            StreamMux.CodecTag = FormatSpec.FindTag(Encoder.CodecSpec.CodecId) ?? throw new($"[#{T}{Stream.Index} - {Stream.CodecId}] Not supported for output");
    }

    public override void MuxerAfterWriteHeader()
        => MaxDuration = Ctx.DurationMcs == NoTs ? long.MaxValue : Rescale(Ctx.DurationMcs, TIME_BASE_Q, StreamMux.Timebase);

    public override void SetupEncoding()
        => SetupEncoding(null);

    public void SetupEncoding(VideoFrame? frame = null)
    {
        VideoEncoderSpec? spec;

        var codecId = Ctx.Options.VideoEncoder != AVCodecID.None ? Ctx.Options.VideoEncoder : Decoder.CodecSpec.CodecId;
        var pixDesc = Decoder.PixelFormat.GetDescriptor();
        var pixFmt  = Decoder.PixelFormat;
        var encoders= CodecSpec.FindVideoEncoders(codecId) ?? throw new($"Could not find encoder spec for '{codecId}'");
        spec        = encoders.Where(e => e.PixelFormats.Contains(pixFmt) && Ctx.Options.HWWrappers.Contains(e.HWWrapper)).FirstOrDefault();

        if (spec == null)
        {
            if (!pixDesc->flags.HasFlag(PixFmtFlags.Hwaccel))
                throw new($"Could not find encoder spec for '{codecId}'");

            WriteLine($"Could not find hw encoder for '{codecId}' ({Decoder.PixelFormat}), will try sw encoder");
            pixFmt      = hwFrames!.SWPixelFormat;
            spec        = encoders.Where(e => e.PixelFormats.Contains(pixFmt) && Ctx.Options.HWWrappers.Contains(e.HWWrapper)).FirstOrDefault() ?? throw new($"Could not find encoder spec for '{codecId}'");
            hwDownload  = true;
        }

        if (frame != null)
            Encoder = new(spec)
            {
                Threads             = Environment.ProcessorCount, // this will cause more memory usage
                Width               = frame.Width,
                Height              = frame.Height,
                SampleAspectRatio   = frame.SampleAspectRatio != AVRational.Default ? frame.SampleAspectRatio : Decoder.SampleAspectRatio,
                PixelFormat         = pixFmt,

                //FieldOrder          = videoStream.FieldOrder, // docs say libavcodec but ffmpeg set this manually
                ColorPrimaries      = frame.ColorPrimaries,
                ColorSpace          = frame.ColorSpace,
                ColorRange          = frame.ColorRange,
                ChromaLocation      = frame.ChromaLocation,
                ColorTransfer       = frame.ColorTransfer,
                BitsPerRawSample    = Stream.BitsPerRawSample, // min with pixDescr comps[0]->depth
            };
        else
            Encoder = new(spec)
            {
                Threads             = Environment.ProcessorCount,
                Width               = Decoder.Width,
                Height              = Decoder.Height,
                SampleAspectRatio   = VideoStream.SampleAspectRatio,
                PixelFormat         = Decoder.PixelFormat,

                ColorPrimaries      = VideoStream.ColorPrimaries,
                ColorSpace          = VideoStream.ColorSpace,
                ColorRange          = VideoStream.ColorRange,
                ChromaLocation      = VideoStream.ChromaLocation,
                ColorTransfer       = VideoStream.ColorTransfer,
                BitsPerRawSample    = VideoStream.BitsPerRawSample,
            };

        if (!hwDownload)
            Encoder.HWFramesContext = Decoder.HWFramesContext; // CRIT: This is wrong we should create another context?*

        // CFR (Constant Frame Rate) - specified in codecpar
        if (VideoStream.FrameRate.Num > 0 && VideoStream.FrameRate.Den > 0)
        {
            Encoder.FrameRate  = VideoStream.FrameRate;
            Encoder.Timebase   = VideoStream.FrameRate.Inverse();
        }
        else // VFR? todo
            Encoder.Timebase   = VideoStream.GuessedFrameRate.Inverse(); //videoEncoder.Timebase   = videoDecoder.PacketTimebase;
        
        Encoder.Flags |= VideoEncoderFlags.FrameDuration; // ensures CFR, encoder will check also frame durations

        if (Muxer.MuxerSpec.Flags.HasFlag(MuxerSpecFlags.GlobalHeader))
            Encoder.Flags |= VideoEncoderFlags.GlobalHeader; // sets extradata

        Encoder.StrictCompliance = StrictCompliance.Experimental;
        Encoder.Open(Ctx.Options.VideoEncoderOptions).ThrowOnFailure();
        EncodingReady = true;

        if (Ctx.TranscoderByStreamIndex.Values.All(x => x.EncodingReady))
            Ctx.SetupMuxer();
    }

    public override void Transcode(Packet packet)
    {
        FFmpegResult ret;
        if (packet.Size == 0)
            return;

        if ((ret = Decoder.SendPacket(packet)).Failed)
        {
            WriteLine($"[#{T}{Stream.Index}] Decoding send error ({ret})");
            Ctx.Retries = ret.InvalidArgument || ret.NoMemory ? 0 : Ctx.Retries - 1; // Force stop on critical errors
            return; 
        }

        DecodeEncodeMux(packet);
    }
    public override void Drain(Packet packet)
    {
        FFmpegResult ret;
        IsDraining = true;

        if ((ret = Decoder.Drain()).Failed)
        {
            WriteLine($"[#{T}{Stream.Index}] Decoding send error ({ret})");
            Ctx.Retries = 0;
            return; 
        }

        DecodeEncodeMux(packet);

        if ((ret = Encoder.Drain()).Failed)
        {
            WriteLine($"[#{T}{Stream.Index}] Encoding send error ({ret})");
            Ctx.Retries = 0;
            return; 
        }

        EncodeMux(packet);
    }
    void DecodeEncodeMux(Packet packet)
    {
        FFmpegResult ret;

        while (Ctx.Retries > 0)
        {
            ret = Decoder.RecvFrame(Frame);

            if (ret.Success)
            {
                if (!EncodingReady)
                    SetupEncoding(Frame);

                //WriteLine($"[#{T}{StreamMux.Index}] [Decoder-out] pts: {Frame.Pts.McsToTime(Decoder.PacketTimebase)} pbe: {Frame.PtsBest.McsToTime(Decoder.PacketTimebase)}");
                RescaleTimestamp();
                //WriteLine($"[#{T}{StreamMux.Index}] [Encoder-in ] pts: {Frame.Pts.McsToTime(Encoder.Timebase)}");
                Frame.PictureType = AVPictureType.None; // let encoder decide for IPB frames

                //WriteLine($"D - [#{T}{StreamMux.Index}] dts: {Frame.PktDts} pts: {Frame.Pts} best: {Frame.PtsBest}");

                if (hwDownload)
                {
                    Frame.TransferTo(SWFrame).ThrowOnFailure();
                    Frame.CopyPropertiesTo(SWFrame).ThrowOnFailure();
                    ret = Encoder.SendFrame(SWFrame);
                }
                else
                    ret = Encoder.SendFrame(Frame);

                if (ret.Failed)
                {
                    WriteLine($"[#{T}{Stream.Index}] Encoding send error ({ret})");
                    Ctx.Retries = ret.InvalidArgument || ret.NoMemory ? 0 : Ctx.Retries - 1; // Force stop on critical errors
                    return; 
                }

                EncodeMux(packet);
            }
            else if (ret.TryAgain)
                return;
            else
            {
                if (ret.Eof)
                    WriteLine($"[#{T}{Stream.Index}] Decoding completed{(IsDraining ? "" : " unexpectedly")}");
                else
                {
                    WriteLine($"[#{T}{Stream.Index}] Decoding recv error ({ret})");
                    Ctx.Retries--;
                }

                return;
            }
        }
    }
    void EncodeMux(Packet packet)
    {
        FFmpegResult ret;

        while (Ctx.Retries > 0)
        {
            ret = Encoder.RecvPacket(packet);

            if (ret.Success)
            {
                if (Ctx.MuxerReady)
                    Mux(packet);
                else
                {
                    if (Ctx.CachedPackets.Count > Ctx.Options.MaxPacketToInit)
                    {
                        foreach (var transcoder in Ctx.TranscoderByStreamIndex.Values.Where(x => !x.EncodingReady))
                            transcoder.SetupEncoding();

                        Mux(packet);
                    }
                    else
                    {
                        packet.StreamIndex = Stream.Index; // to be able to map it back to transcoder
                        Ctx.CachedPackets.Add(packet.Ref());
                    }
                }
            }
            else if (ret.TryAgain)
                return;
            else
            {
                if (ret.Eof)
                    WriteLine($"[#{T}{Stream.Index}] Encoding completed{(IsDraining ? "" : " unexpectedly")}");
                else
                {
                    WriteLine($"[#{T}{Stream.Index}] Encoding recv error ({ret})");
                    Ctx.Retries--;
                }

                return;
            }
        }
    }
    public override void Mux(Packet packet)
    {
        //WriteLine($"[#{T}{StreamMux.Index}] [Encoder-out] dts: {packet.Dts.McsToTime(Encoder.Timebase)} pts: {packet.Pts.McsToTime(Encoder.Timebase)} | {Duration.McsToTime(Encoder.Timebase)} <> {(MaxDuration == long.MaxValue ? "∞" : MaxDuration.McsToTime(Encoder.Timebase))}");
        packet.RescaleTimestamp(Encoder.Timebase, StreamMux.Timebase, StreamMux.Index);
        WriteLine($"[#{T}{StreamMux.Index}] [Muxer-final] dts: {packet.Dts.TbToTime(StreamMux.Timebase)} pts: {packet.Pts.TbToTime(StreamMux.Timebase)} | {Duration.TbToTime(StreamMux.Timebase)} <> {(MaxDuration == long.MaxValue ? "∞" : MaxDuration.TbToTime(StreamMux.Timebase))}");
        
        if (Duration > MaxDuration)
        {
            Demuxer.Disable(Stream);
            if (Demuxer.Streams.All(s => !s.Enabled) || Demuxer.Streams.Where(s => s.Enabled).All(s => s.CodecId == AVCodecID.Mjpeg))
                { Ctx.Retries = 0; return; }
        }

        Duration += packet.Duration;
        if (Muxer.WritePacketInterleaved(packet).Failed)
            Ctx.Retries--;
    }

    public void RescaleTimestamp()
    {
        if (Frame.Pts == NoTs)
            return;

        Frame.Duration   = Rescale(Frame.Duration,          Decoder.Timebase, Encoder.Timebase);
        Frame.Pts        = Rescale(Frame.Pts - StartTimePts,Decoder.Timebase, Encoder.Timebase);

        // Fix the issue with time bases that it will produce the same pts
        if (Frame.Pts == LastPts)
            Frame.Pts = ++LastPts;
        else
            LastPts = Frame.Pts;
    }

    public override void Dispose()
    {
        Demuxer?. Disable(Stream);
        Decoder?. Dispose();
        Encoder?. Dispose();
        Frame?.   Dispose();

        hwDevice?.Dispose();
        hwFrames?.Dispose();
    }
}
