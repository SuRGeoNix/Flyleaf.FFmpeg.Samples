LoadFFmpeg();

TranscoderSample.Run(new()
{
    InputFile   = Sample,
    OutputFile  = AVI,
    //HWDevice    = AVHWDeviceType.D3d11va,
    ExcludeStreams = [1,2],
    //StartTime   = TimeSpan.FromSeconds(120)
    //Duration = TimeSpan.FromMinutes(20)
    //VideoDecoderOptions = new() { ["hwaccel_flags"] = "allow_profile_mismatch" }, // https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/options_table.h
    VideoEncoder= AVCodecID.H264,
    //AudioEncoder= AVCodecID.Mp3,
});

//TranscoderSample.Run(new()
//{
//    InputFile   = Bluray,
//    OutputFile  = UDP,
//    Duration    = TimeSpan.FromSeconds(210),
//    Streaming   = true,
//});


/// <summary>
/// Forces transcode -no remux- for all streams
/// </summary>
unsafe class TranscoderSample
{
    public static void Run(TranscoderOptions opt)
        => _ = new TranscoderSample(opt);

    public class TranscoderOptions
    {
        public required string  InputFile       { get; init; } = null!;
        public required string  OutputFile      { get; init; } = null!;

        public TimeSpan         StartTime       { get; init; } = TimeSpan.Zero;
        public TimeSpan         Duration        { get; init; } = TimeSpan.FromSeconds(30); // This is at least (in case of video starting later than audio this will increase the final duration by diff)
        public int              Retries         { get; init; } = 50;
        public int              MaxPacketToInit { get; init; } = 50;    // For proper initialization of the AV encoders at least one frame should be provided
        public bool             Streaming       { get; init; } = false; // Waits based on the time passed
        public List<int>?       IncludeStreams  { get; init; } = null;
        public List<int>?       ExcludeStreams  { get; init; } = null;
        public AVHWDeviceType   HWDevice        { get; init; } = AVHWDeviceType.None;
        public List<HWWrapper>  HWWrappers      { get; init; } = [HWWrapper.None, HWWrapper.Other, HWWrapper.D3D12, HWWrapper.Nvidia]; // Mainly to 'blacklist' GPU vendors (Amd/Intel/NVidia) as currently is not possible to check the adapter used by hw device

        public AVCodecID        AudioEncoder    { get; init; } = AVCodecID.None;
        public AVCodecID        VideoEncoder    { get; init; } = AVCodecID.None;

        public Dictionary<string, string>? DemuxerOptions       { get; init; }
        public Dictionary<string, string>? MuxerOptions         { get; init; }
        public Dictionary<string, string>? AudioDecoderOptions  { get; init; }
        public Dictionary<string, string>? AudioEncoderOptions  { get; init; }
        public Dictionary<string, string>? VideoDecoderOptions  { get; init; }
        public Dictionary<string, string>? VideoEncoderOptions  { get; init; }
        
    }

    public TranscoderOptions    Options;
    public long                 StartTimeMcs;       // Demuxer's    StartTime
    public long                 SeekTimeMcs;        // User's       StartTime
    public long                 DurationMcs;
    public int                  Retries;

    public Demuxer              Demuxer = null!;
    public Muxer                Muxer   = null!;
    public bool                 MuxerReady;         // Headers Written

    public List<Packet>         CachedPackets = []; // Until MuxerReady
    public Dictionary<int, Transcoder>
                                TranscoderByStreamIndex = [];
    
    Transcoder? mainTranscoder;
    Stopwatch sw = new(); // for streaming

    TranscoderSample(TranscoderOptions opt)
    {
        Options = opt;
        
        #region Open / Analyze / Dump Demuxer (InputFile)
        Demuxer = new()
        {
            Flags           = DemuxerFlags.DiscardCorrupt,
            MaxProbeBytes   = 500 * 1024 * 1024,
            MaxAnalyzeMcs   = (long)TimeSpan.FromSeconds(500).TotalMicroseconds
        };
        Demuxer.Open(Options.InputFile, opts: Options.DemuxerOptions).ThrowOnFailure();
        Demuxer.Analyse();
        Demuxer.Dump(Options.InputFile);

        Packet packet   = new();
        Retries         = Options.Retries;
        StartTimeMcs    = Demuxer.StartTimeMcs != NoTs ? Demuxer.StartTimeMcs : 0;
        SeekTimeMcs     = (long)Options.StartTime.TotalMicroseconds;
        DurationMcs     = Options.Duration == TimeSpan.Zero ? NoTs : (long)Options.Duration.TotalMicroseconds;
        #endregion

        #region Initialize Muxer (OutputFile)
        MuxerSpec muxerSpec = MuxerSpec.FindMuxer(Options.OutputFile) ?? throw new($"Could not find valid output format for {Options.OutputFile}"); // before encoders to check global header
        Muxer = new(muxerSpec, Options.OutputFile);
        #endregion

        #region Create AVS Transcoders (Per Stream)
        foreach (var stream in Demuxer.Streams)
        {
            if ((Options.IncludeStreams != null && !Options.IncludeStreams.Contains(stream.Index)) ||
                (Options.ExcludeStreams != null && Options.ExcludeStreams.Contains(stream.Index)))
                continue;

            var transcoder = Transcoder.Create(this, stream);
            if (transcoder == null)
            {
                switch (stream._codecpar->codec_type)
                {
                    case AVMediaType.Audio:
                    case AVMediaType.Video:
                        throw new("Audio / Video setup failed");
                        
                    default: // subs can be ignored
                        break;
                }
            }
            else
                TranscoderByStreamIndex[stream.Index] = transcoder;
        }
        #endregion

        #region Transcode
        if (SeekTimeMcs != 0) // initial seek to (demuxer's start time +) user's start time
            Demuxer.Seek(StartTimeMcs + SeekTimeMcs, -1, SeekFlags.Frame).ThrowOnFailure();

        FFmpegResult ret;

        if      ((mainTranscoder = TranscoderByStreamIndex.Values.Where(x => x is VideoTranscoder).FirstOrDefault()) == null)
            if  ((mainTranscoder = TranscoderByStreamIndex.Values.Where(x => x is AudioTranscoder).FirstOrDefault()) == null)
                throw new("No audio/video");

        sw.Start();
        while ((ret = Demuxer.ReadPacket(packet)).Success && Retries > 0)
        {
            if (TranscoderByStreamIndex.TryGetValue(packet.StreamIndex, out var transcoder))
                transcoder.Transcode(packet);

            if (Options.Streaming && MuxerReady)
            {
                var diff = ((AVTranscoder)mainTranscoder!).Duration.ToMs(mainTranscoder.StreamMux.Timebase) - sw.ElapsedMilliseconds;
                if (diff > 2000)
                    Thread.Sleep(500);
            }
        }

        if (ret.Failed & !ret.Eof)
            WriteLine("Demuxer stopped with errors");

        if (Retries > 0) // Draining (Decoders / Encoders / Muxer)
            foreach (var transcoder in TranscoderByStreamIndex.Values)
                transcoder.Drain(packet);
        #endregion

        #region Dispose
        if (MuxerReady)
            Muxer.WriteTrailer().ThrowOnFailure();
        
        foreach(var transcoder in TranscoderByStreamIndex.Values)
            transcoder.Dispose();

        Muxer.Dispose();
        packet.Dispose();
        Demuxer.Dispose();
        //FFProbe(Options.OutputFile);
        #endregion
    }

    // Will be called by the last transcoder when all transcoders are ready (have been configured by at least one frame or cached packets exhausted)
    public void SetupMuxer()
    {
        if (SeekTimeMcs != 0) // (ideally first frame's timestamp so we do it manually)
            Muxer.AvoidNegTSFlags = AvoidNegTSFlags.MakeZero; // Try to start from 0 timestamps

        // Set muxer streams (by V / A / S priority)
        foreach(var transcoder in TranscoderByStreamIndex.Values.Where(x => x is VideoTranscoder))
            transcoder.MuxerBeforeWriteHeader();

        foreach(var transcoder in TranscoderByStreamIndex.Values.Where(x => x is AudioTranscoder))
            transcoder.MuxerBeforeWriteHeader();

        foreach(var transcoder in TranscoderByStreamIndex.Values.Where(x => x is SubtitleTranscoder))
            transcoder.MuxerBeforeWriteHeader();

        Muxer.WriteHeader(Options.MuxerOptions).ThrowOnFailure();

        Muxer.Dump(Options.OutputFile);
        MuxerReady = true;

        // Update transcoders based on new mux streams values (mainlly EndTimePts)
        foreach(var transcoder in TranscoderByStreamIndex.Values)
            transcoder.MuxerAfterWriteHeader();

        // Mux cached packets
        foreach (var packet in CachedPackets)
        {
            if (TranscoderByStreamIndex.TryGetValue(packet.StreamIndex, out var transcoder))
                transcoder.Mux(packet);
            else
                continue;

            packet.Dispose();
        }
    }
}
