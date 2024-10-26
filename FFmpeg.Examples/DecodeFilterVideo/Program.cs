using Flyleaf.FFmpeg.Format;
using Flyleaf.FFmpeg.Format.Demux;
using Flyleaf.FFmpeg;
using Flyleaf.FFmpeg.Spec;

using Flyleaf.FFmpeg.Codec.Decode;
using Flyleaf.FFmpeg.Codec;
using Flyleaf.FFmpeg.Filter;

using static Flyleaf.FFmpeg.Raw;
using static Common.Utils;

LoadFFmpeg();

DecodeFilterVideoSample.Run(new()
{
    InputFile   = Sample,
    OutputFile  = RAWVideo
});

public unsafe class DecodeFilterVideoSample
{
    public static void Run(DecodeFilterVideoOptions opt)
        => _ = new DecodeFilterVideoSample(opt);

    public class DecodeFilterVideoOptions
    {
        public required string  InputFile       { get; init; } = null!;
        public required string  OutputFile      { get; init; } = null!;
        public long             MaxFrames       { get; init; } = 100;
    }

    DecodeFilterVideoSample(DecodeFilterVideoOptions opt)
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
        long maxFrames = opt.MaxFrames;
        #endregion

        #region Find Video Stream / Enable Video Stream / Find Video Decoder / Open Video Decoder / Prepare Output
        VideoStream         videoStream     = demuxer.BestVideoStream() ?? throw new Exception("Could not find video stream");
        demuxer.Enable(videoStream); // Activating stream for demuxing. By default all streams will be de-activated (disabled)

        VideoDecoderSpec    videoCodec      = CodecSpec.FindVideoDecoder(videoStream.CodecId) ?? throw new Exception($"Could not find codec for '{videoStream.CodecId}' id");
        VideoDecoder        videoDecoder    = new(videoCodec, videoStream);
        videoDecoder.Open().ThrowOnFailure();
        
        VideoFrame          videoFrame      = new();
        FileStream          videoFile       = new(opt.OutputFile, FileMode.Create, FileAccess.Write);
        #endregion

        #region Filter
        FilterGraph         filterGraph     = new();
        VideoBufferSource   videoBufferSrc  = new(filterGraph, new(videoDecoder));
        FilterContext       scale           = new(filterGraph, "scale", "scale0", "78:24");
        FilterContext       transpose       = new(filterGraph, "transpose", "transpose0", "cclock");
        VideoBufferSink     videoBufferSink = new(filterGraph, new()
        {
            PixelFormats = [AVPixelFormat.Gray8]
        });

        videoBufferSrc.
            Link(scale).
            Link(transpose).
            Link(videoBufferSink);

        filterGraph.Config();

        VideoFrame videoFiltFrame = new();
        #endregion

        #region Demux Packet / Decode Frame / Write Raw Frame
        FFmpegResult ret;
        while (demuxer.ReadPacket(packet).Success && maxFrames > 0) // No draining in this sample
        {
            if (packet.StreamIndex != videoStream.Index)
                continue;

            videoDecoder.SendPacket(packet).ThrowOnFailure();
                
            while(true)
            {
                ret = videoDecoder.RecvFrame(videoFrame);

                if (ret.Success)
                {
                    videoBufferSrc.SendFrame(videoFrame).ThrowOnFailure();

                    while (true)
                    {
                        ret = videoBufferSink.RecvFrame(videoFiltFrame);

                        if (ret.Success)
                        {
                            byte[] frameBytes = videoFiltFrame.ToRawImage();
                            videoFile.Write(frameBytes, 0, frameBytes.Length);
                        }
                        else if (!ret.TryAgain && !ret.Eof)
                            ret.ThrowOnFailure();
                        else
                            break;
                    }

                    maxFrames--;
                }
                else if (!ret.TryAgain && !ret.Eof)
                    ret.ThrowOnFailure();
                else
                    break;
            }
        }
        #endregion

        Console.WriteLine($"Open with Flyleaf -> fmt://rawvideo?{opt.OutputFile}&pixel_format=gray8&video_size=24x78");

        #region Dispose
        demuxer.Dispose();
        videoDecoder.Dispose();
        packet.Dispose();
        videoFrame.Dispose();
        videoFiltFrame.Dispose();
        filterGraph.Dispose();
        videoFile.Dispose();
        #endregion
    }
}