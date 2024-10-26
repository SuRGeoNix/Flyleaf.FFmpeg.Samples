unsafe abstract class Transcoder : IDisposable
{
    public TranscoderSample Ctx;
    public Demuxer          Demuxer;
    public Muxer            Muxer;
    public MediaStream      Stream;
    public MediaStreamMux   StreamMux       = null!;

    public long             StartTimePts;               // (Encoder TB)     Subtracting the demuxer's start time from the frame ensures that we will not run in ts overflow during rescales and that the output format will start from 0
    public long             MaxDuration;                // (StreamMux TB)   
    public bool             EncodingReady   = false;    // When all ready for encoding muxer can be ready too
    public bool             IsDraining;

    protected Transcoder(TranscoderSample transcode, MediaStream stream, long startTimePts)
    {
        Ctx         = transcode;
        Muxer       = transcode.Muxer;
        Demuxer     = transcode.Demuxer;
        Stream      = stream;
        StartTimePts= startTimePts;

        Demuxer.Enable(stream);
    }

    public static Transcoder? Create(TranscoderSample transcode, MediaStream stream)
        => stream._codecpar->codec_type switch
    {
        AVMediaType.Audio       => AudioTranscoder.     Create(transcode, (AudioStream)stream),
        AVMediaType.Video       => VideoTranscoder.     Create(transcode, (VideoStream)stream),
        AVMediaType.Subtitle    => SubtitleTranscoder.  Create(transcode, (SubtitleStream)stream),
        _ => null,
    };

    public abstract void MuxerBeforeWriteHeader();
    public abstract void MuxerAfterWriteHeader();
    public abstract void Mux(Packet packet);

    public abstract void SetupEncoding();

    public abstract void Transcode(Packet packet);
    public abstract void Drain(Packet packet);

    public abstract void Dispose();
}

unsafe abstract class AVTranscoder(TranscoderSample transcode, MediaStream stream, long startTimePts) : Transcoder(transcode, stream, startTimePts)
{
    public long             LastPts     = NoTs; // (Encoder TB)     During Frame Pts Rescale (from Decoder to Encoder) ensures that we will have pts > prev_pts
    public long             Duration;           // (StreamMux TB)
}
