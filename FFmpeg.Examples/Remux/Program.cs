using Flyleaf.FFmpeg.Format;
using Flyleaf.FFmpeg.Format.Demux;
using Flyleaf.FFmpeg.Format.Mux;
using Flyleaf.FFmpeg.Spec;

using static Common.Utils;

LoadFFmpeg();

RemuxerSample.Run(new()
{
    InputFile = Sample,
    OutputFile = TS
});

public class RemuxerSample
{
    public static void Run(RemuxerOptions opt)
        => _ = new RemuxerSample(opt);

    public class RemuxerOptions
    {
        public required string  InputFile       { get; init; } = null!;
        public required string  OutputFile      { get; init; } = null!;
        public long             MaxPackets      { get; init; } = 1000;
    }

    RemuxerSample(RemuxerOptions opt)
    {
        #region Open / Analyze / Dump Demuxer
        Demuxer demuxer = new()
        {
            MaxProbeBytes = 100 * 1024 * 1024,
            MaxAnalyzeMcs = (long)TimeSpan.FromSeconds(5).TotalMicroseconds
        };
        demuxer.Open(opt.InputFile);
        demuxer.Analyse();
        demuxer.Dump(opt.InputFile);
        Packet packet = new();
        long maxPackets = opt.MaxPackets;
        #endregion

        #region Configure & Set Streams / Write Header / Dump Muxer
        var muxerSpec = MuxerSpec.FindMuxer(opt.OutputFile) ?? throw new Exception($"Could not find valid output format for {opt.OutputFile}");
        Muxer muxer = new(muxerSpec, opt.OutputFile)
        {
            OutputTSOffset = demuxer.StartTimeMcs > 0 ? -demuxer.StartTimeMcs : 0, // ensures we start the output format from 0 ts (except if the selected streams start later on or seeked, should get the the first/min ts from selected)
        };

        Dictionary<MediaStream, MediaStreamMux> inOutStreamsMap = [];
        foreach(var stream in demuxer.Streams)
        {
            if (stream is VideoStream videoStream)
            {
                VideoStreamMux videoStreamMux = new(muxer, videoStream);
                inOutStreamsMap[stream] = videoStreamMux;
                demuxer.Enable(stream);
            }
            else if (stream is AudioStream audioStream)
            {
                AudioStreamMux audioStreamMux = new(muxer, audioStream);
                inOutStreamsMap[stream] = audioStreamMux;
                demuxer.Enable(stream);
            }
            else if (stream is SubtitleStream subStream) // bin / private data if not supported
            {
                SubtitleStreamMux subStreamMux = new(muxer, subStream);
                inOutStreamsMap[stream] = subStreamMux;
                demuxer.Enable(stream);
            }
        }
        muxer.WriteHeader().ThrowOnFailure();
        muxer.Dump(opt.OutputFile);
        #endregion

        #region Remuxing (Demux -> (Rescale) -> Mux)
        while (demuxer.ReadPacket(packet).Success && maxPackets > 0)
        {
            var instream = demuxer.Streams[packet.StreamIndex]; // demuxer.Streams ensures keeping the same index as the avstreams
            if (!inOutStreamsMap.TryGetValue(instream, out var outstream))
                continue;

            maxPackets--;
            packet.RescaleTimestamp(instream.Timebase, outstream.Timebase, outstream.Index); // Rescales Timestamps to Output Stream and Updates Stream Index
            if (muxer.WritePacketInterleaved(packet).Failed)
                throw new Exception("Failed to mux packets");
        }
        #endregion

        #region Write Trailer / Dispose
        demuxer.Dispose();
        packet.Dispose();
        muxer.WriteTrailer();
        muxer.Dispose();
        FFProbe(opt.OutputFile);
        #endregion

        Console.WriteLine($"[Success] {(new FileInfo(opt.OutputFile)).FullName}");
    }
}