unsafe class AudioTranscoder : AVTranscoder
{
    public AudioDecoder     Decoder     = null!;
    public AudioEncoder     Encoder     = null!;
    public AudioFrame       Frame       = new();
    public AudioStream      AudioStream = null!;

    const string T = "A";

    AudioTranscoder(TranscoderSample ctx, AudioStream stream) : base(ctx, stream, Rescale(ctx.StartTimeMcs, TIME_BASE_Q, stream.Timebase)) { AudioStream = stream; }

    public static AudioTranscoder? Create(TranscoderSample ctx, AudioStream stream)
    {
        AudioTranscoder transcoder = new(ctx, stream);
        
        if (!transcoder.SetupDecoding())
        {
            transcoder.Dispose();
            return null;
        }

        return transcoder;
    }

    bool SetupDecoding()
    {
        AudioDecoderSpec? spec = CodecSpec.FindAudioDecoder(Stream.CodecId);

        if (spec == null)
        {
            WriteLine($"[#{Stream.Index} - {Stream.CodecId}] Could not find decoder spec");
            return false;
        }

        Decoder = new(spec, AudioStream);

        FFmpegResult ret;
        if ((ret = Decoder.Open(Ctx.Options.AudioDecoderOptions)).Failed)
        {
            WriteLine($"[#{Stream.Index} - {Stream.CodecId}] Could not open the decoder ({ret})");
            return false; 
        }

        return true;
    }

    public override void MuxerBeforeWriteHeader()
    {
        StreamMux = new AudioStreamMux(Muxer, Encoder);
        string? lang = Stream.MetadataGet("language");
        if (lang != null)
            StreamMux.MetadataSet("language", lang);
    }

    public override void MuxerAfterWriteHeader()
        => MaxDuration = Ctx.DurationMcs == NoTs ? long.MaxValue : Rescale(Ctx.DurationMcs, TIME_BASE_Q, StreamMux.Timebase);

    public override void SetupEncoding()
        => SetupEncoding(null);

    public void SetupEncoding(AudioFrame? frame = null)
    {
        var codecId = Ctx.Options.AudioEncoder != AVCodecID.None ? Ctx.Options.AudioEncoder : Decoder.CodecSpec.CodecId;
        var encoders= CodecSpec.FindAudioEncoders(codecId) ?? throw new($"Could not find encoder spec for '{codecId}'");
        var spec    = encoders.Where(ae => ae.SampleFormats.Contains(Decoder.SampleFormat)).FirstOrDefault() ?? throw new($"Could not find encoder spec for '{codecId}'");

        if (frame != null)
            Encoder = new(spec)
            {
                ChannelLayout       = frame.ChannelLayout,
                SampleFormat        = frame.SampleFormat,        //audioEncoderSpec.SampleFormats.Count > 0 ? audioEncoderSpec.SampleFormats[0] : audioDecoder.SampleFormat, // we must ensure we have the same format as the decoder
                SampleRate          = frame.SampleRate,
                Timebase            = new(1, frame.SampleRate)   //audioDecoder.PacketTimebase, //new(1, audioDecoder.SampleRate) // maybe when we use different codec from source
                //Timebase            = audioDecoder.PacketTimebase
            };
        else
            Encoder = new(spec)
            {
                ChannelLayout       = Decoder.ChannelLayout,
                SampleFormat        = Decoder.SampleFormat,
                SampleRate          = Decoder.SampleRate,
                Timebase            = new(1, Decoder.SampleRate)
            };

        // Should choose the right spec also based on codec profile (frame size might differ) (eg. AAC-LC / HE-AAC)
        //if (audioEncoderSpec.CodecId == audioCodec.CodecId)
        //{
        //    audioEncoder.CodecProfile   = audioDecoder.CodecProfile;
        //    audioEncoder.Level          = audioDecoder.Level;
        //}

        if (Muxer.FormatSpec.Flags.HasFlag(MuxerSpecFlags.GlobalHeader))
            Encoder.Flags |= AudioEncoderFlags.GlobalHeader;

        Encoder.Flags |= AudioEncoderFlags.FrameDuration;
        Encoder.StrictCompliance = StrictCompliance.Experimental;
        Encoder.Open(Ctx.Options.AudioEncoderOptions).ThrowOnFailure();

        // TODO: Requires re-sampling
        if ((frame != null && Encoder.FrameSize != frame.Samples     && frame.Samples > 0) ||
            (frame == null && Encoder.FrameSize != Decoder.FrameSize && Decoder.FrameSize > 0))
            throw new($"Could not find encoder spec for '{codecId}' (different profiles? frame_size)");

        EncodingReady = true;

        if (Ctx.TranscoderByStreamIndex.Values.All(x => x.EncodingReady))
            Ctx.SetupMuxer();
    }

    public override void Transcode(Packet packet)
    {
        FFmpegResult ret;

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

                if ((ret = Encoder.SendFrame(Frame)).Failed)
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
        packet.RescaleTimestamp(Encoder.Timebase, StreamMux.Timebase, StreamMux.Index);
        WriteLine($"[#{T}{StreamMux.Index}] [Muxer-final] dts: {packet.Dts.McsToTime(StreamMux.Timebase)} pts: {packet.Pts.McsToTime(StreamMux.Timebase)} | {Duration.McsToTime(StreamMux.Timebase)} <> {(MaxDuration == long.MaxValue ? "∞" : MaxDuration.McsToTime(StreamMux.Timebase))}");

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

        Frame.Duration   = Rescale(Frame.Duration,          Decoder.PacketTimebase, Encoder.Timebase);
        Frame.Pts        = Rescale(Frame.Pts - StartTimePts,Decoder.PacketTimebase, Encoder.Timebase);

        // Fix the issue with time bases that it will produce the same pts
        if (Frame.Pts == LastPts)
            Frame.Pts = ++LastPts;
        else
            LastPts = Frame.Pts;
    }

    public override void Dispose()
    {
        Demuxer?.Disable(Stream);
        Decoder?.Dispose();
        Encoder?.Dispose();
        Frame?.Dispose();
    }
}
