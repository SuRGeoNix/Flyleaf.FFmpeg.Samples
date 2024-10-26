unsafe class SubtitleTranscoder : Transcoder
{
    public SubtitleDecoder  Decoder = null!;
    public SubtitleEncoder  Encoder = null!;
    public SubtitleFrame    Frame   = new();

    const string T = "S";

    SubtitleTranscoder(TranscoderSample ctx, SubtitleStream stream) : base(ctx, stream, ctx.StartTimeMcs) { } // Rescale for A/V

    public static SubtitleTranscoder? Create(TranscoderSample ctx, SubtitleStream stream)
    {
        SubtitleDecoderSpec? spec = CodecSpec.FindSubtitleDecoder(stream.CodecId);
        if (spec == null)
        {
            WriteLine($"[#{stream.Index} - {stream.CodecId}] Could not find decoder spec");
            return null;
        }
            
        SubtitleTranscoder transcoder = new(ctx, stream)
        {
            Decoder = new(spec, stream),
        };

        FFmpegResult ret;
        if ((ret = transcoder.Decoder.Open()).Failed)
        {
            transcoder.Dispose();
            WriteLine($"[#{stream.Index} - {stream.CodecId}] Could not open the decoder ({ret})");
            return null;
        }

        // For subs we don't wait for frame to setup the encoding (as this could cause a large cached packets queue until we have one)
        transcoder.SetupEncoding();

        if (!transcoder.EncodingReady)
        {
            transcoder.Dispose();
            return null;
        }

        return transcoder;
    }

    public override void MuxerBeforeWriteHeader()
    {
        StreamMux = new SubtitleStreamMux(Muxer, Encoder);
        string? lang = Stream.MetadataGet("language");
        if (lang != null)
            StreamMux.MetadataSet("language", lang);
    }

    public override void MuxerAfterWriteHeader()
        => MaxDuration = Ctx.DurationMcs == NoTs ? long.MaxValue : Rescale(Ctx.DurationMcs + Ctx.SeekTimeMcs, TIME_BASE_Q, StreamMux.Timebase);

    public override void SetupEncoding()
    {
        if (Decoder.CodecDescriptor == null)
            return;

        SubtitleEncoderSpec? subEncoderSpec = null;
        if (Muxer.FormatSpec.Supports(Decoder.CodecSpec.CodecId))
            subEncoderSpec = CodecSpec.FindSubtitleEncoder(Decoder.CodecSpec.CodecId);
        else if (Decoder.CodecSpec.CodecId == AVCodecID.HdmvPgsSubtitle && (Muxer.FormatSpec.Name == "matroska" || Muxer.FormatSpec.Supports(AVCodecID.DvdSubtitle)))
        {
            subEncoderSpec = CodecSpec.FindSubtitleEncoder(AVCodecID.DvdSubtitle);
            WriteLine($"[#{T}{Stream.Index} - {Stream.CodecId}] Not supported for output. Using dvdsubtitles instead");
        }
        else if (Decoder.CodecDescriptor.Properties.HasFlag(CodecPropFlags.TextSub))
            subEncoderSpec = CodecSpec.FindSubtitleEncoder(Muxer.FormatSpec.BestTextSubtitleEncoder());

        if (subEncoderSpec == null)
        {
            WriteLine($"[#{T}{Stream.Index} - {Stream.CodecId}] Not supported for output");
            return;
        }

        Encoder = new(subEncoderSpec)
        {
            // Bitmap only
            Width       = Decoder.Width,
            Height      = Decoder.Height,

            Timebase    = TIME_BASE_Q, // fixed
        };
                        
        // ASS only?
        Decoder.HeaderDataCopyTo(&Encoder._ptr->subtitle_header, &Encoder._ptr->subtitle_header_size); // TODO: Pass from decoder? (and w/h from stream*)

        Decoder.ExtraDataCopyTo(&Encoder._ptr->extradata, &Encoder._ptr->extradata_size);

        if (Muxer.FormatSpec.Flags.HasFlag(MuxerSpecFlags.GlobalHeader))
            Encoder.Flags |= SubtitleEncoderFlags.GlobalHeader;

        Encoder.Flags |= SubtitleEncoderFlags.FrameDuration;
        if (Encoder.Open().Failed)
            return;
                
        EncodingReady = true;
    }

    public override void Transcode(Packet packet)
    {
        FFmpegResult ret;
        Frame.Reset();
        (ret, bool gotFrame) = Decoder.SendRecvFrame(packet, Frame);
        ret.ThrowOnFailure();

        if (!gotFrame || Frame.Pts == NoTs)
            return;

        // TBR: from pgs to dvd (we need to set prev duration here)
        if (Frame.RectsNum < 1 && Encoder.CodecSpec.CodecId == AVCodecID.DvdSubtitle)
            return;

        // Frame pts always in Mcs and start/end display time in Ms
        Encoder.SendRecvPacket(Frame, out Packet subEncPkt).ThrowOnFailure();
        subEncPkt.Pts = subEncPkt.Dts = subEncPkt.Pts - StartTimePts;
        
        if (Ctx.MuxerReady)
            Mux(subEncPkt);
        else
        {
            subEncPkt.StreamIndex = Stream.Index; // to be able to map it back to transcoder
            Ctx.CachedPackets.Add(subEncPkt.Ref());
        }
    }
    public override void Mux(Packet packet)
    {
        packet.RescaleTimestamp(Encoder.Timebase, StreamMux.Timebase, StreamMux.Index);
        WriteLine($"[#{T}{StreamMux.Index}] [Muxer-final] dts: {packet.Dts.McsToTime(StreamMux.Timebase)} pts: {packet.Pts.McsToTime(StreamMux.Timebase)} <> {(MaxDuration == long.MaxValue ? "∞" : MaxDuration.McsToTime(StreamMux.Timebase))}");

        if (packet.Pts + packet.Duration > MaxDuration)
        {
            if (packet.Pts < MaxDuration)
            {
                packet.Duration = MaxDuration - packet.Pts;
                Muxer.WritePacketInterleaved(packet);
            }

            Demuxer.Disable(Stream);
            if (Demuxer.Streams.All(s => !s.Enabled) || Demuxer.Streams.Where(s => s.Enabled).All(s => s.CodecId == AVCodecID.Mjpeg))
                Ctx.Retries = 0;

            return;
        }

        Muxer.WritePacketInterleaved(packet);
    }

    public override void Drain(Packet packet) { IsDraining = true; }

    public override void Dispose()
    {
        Demuxer?.Disable(Stream);
        Decoder?.Dispose();
        Encoder?.Dispose();
        Frame?.Dispose();
    }
}
