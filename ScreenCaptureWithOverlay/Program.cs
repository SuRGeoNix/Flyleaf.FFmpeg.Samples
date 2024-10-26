using Flyleaf.FFmpeg;
using Flyleaf.FFmpeg.Codec;
using Flyleaf.FFmpeg.Codec.Decode;
using Flyleaf.FFmpeg.Format;
using Flyleaf.FFmpeg.Format.Demux;
using Flyleaf.FFmpeg.Spec;

using static Flyleaf.FFmpeg.Raw;
using static Common.Utils;
using Flyleaf.FFmpeg.Filter;
using Flyleaf.FFmpeg.Format.Mux;
using Flyleaf.FFmpeg.Codec.Encode;

LoadFFmpeg();

ScreenCapOverlay.Run(new()
{
    ImageFile   = Coca,
    OutputFile  = TS,
});

/// <summary>
/// Creates a video file by capturing the desktop and applying an overlay from an image
/// </summary>
public unsafe class ScreenCapOverlay
{
    /* TODO
     * HW process with overlay_cuda
     * Logo scaling / positioning
     */

    public static void Run(HWDecodeOptions opt)
        => _ = new ScreenCapOverlay(opt);

    public class HWDecodeOptions
    {
        public required string  ImageFile       { get; init; } = null!;
        public required string  OutputFile      { get; init; } = null!;
        public int              Framerate       { get; init; } = 25;
        public long             MaxFrames       { get; init; } = 100;
    }

    ScreenCapOverlay(HWDecodeOptions opt)
    {
        long            maxFrames       = opt.MaxFrames;
        VideoFrame      imageFrame      = GetImage(opt.ImageFile);
        OverlayFrames   overlayFrames   = new(imageFrame, opt.Framerate);
        VideoFrame      videoFrame      = new();
        overlayFrames.RecvFrame(videoFrame); // used only for configuration
        
        MuxerSpec       muxerSpec       = MuxerSpec.FindMuxer(opt.OutputFile) ?? throw new Exception($"Could not find valid output format for {opt.OutputFile}"); // before encoders to check global header
        Muxer           muxer           = new(muxerSpec, opt.OutputFile);

        var videoEncoderSpec = CodecSpec.FindVideoEncoder("libx264rgb") ?? throw new Exception($"Could not find libx264 encoder");
        VideoEncoder videoEncoder = new(videoEncoderSpec)
        {
            Threads             = Environment.ProcessorCount,
            Width               = videoFrame.Width,
            Height              = videoFrame.Height,
            SampleAspectRatio   = overlayFrames.SinkConfiguration.SampleAspectRatio,
            PixelFormat         = videoFrame.PixelFormat,
            Timebase            = overlayFrames.SinkConfiguration.Timebase,
            //FrameRate           = overlayFrames.SinkConfiguration.FrameRate,
            ColorRange          = videoFrame.ColorRange,
            ColorSpace          = videoFrame.ColorSpace,
            
            GopSize             = opt.Framerate * 5,
            BitRate             = 625 * 1000 * 8,
            //BufSize             = 625 * 1000 * 8 * 100,

            // TBR: this should be set from the frame (or even better all info from frame)
            ColorPrimaries      = videoFrame.ColorPrimaries,
            ColorTransfer       = videoFrame.ColorTransfer,
            ChromaLocation      = videoFrame.ChromaLocation,
            FieldOrder          = AVFieldOrder.Progressive,
        };

        if (muxerSpec.Flags.HasFlag(MuxerSpecFlags.GlobalHeader))
            videoEncoder.Flags |= VideoEncoderFlags.GlobalHeader;

        videoEncoder.Flags |= VideoEncoderFlags.FrameDuration;
        Dictionary<string, string> encoderOptions = new()
        {
            //{ "preset",     "veryslow" },
            //{ "crf",        "18" },
            //{ "qp",         "0" }
        };
        videoEncoder.Open(encoderOptions);

        VideoStreamMux videoStreamMux = new(muxer, videoEncoder);
        muxer.WriteHeader();
        muxer.Dump(opt.OutputFile);
        
        Packet packet = new();
        FFmpegResult ret;
        FFmpegResult ret2;

        while((ret2 = overlayFrames.RecvFrame(videoFrame)).Success)
        {
            videoEncoder.SendFrame(videoFrame).ThrowOnFailure();

            while (true)
            {
                ret = videoEncoder.RecvPacket(packet);

                if (ret.Success)
                {
                    packet.RescaleTimestamp(videoEncoder.Timebase, videoStreamMux.Timebase, videoStreamMux.Index);
                    muxer.WritePacketInterleaved(packet);
                }
                else if (!ret.TryAgain && !ret.Eof)
                    ret.ThrowOnFailure();
                else
                    break;
            }

            if (--maxFrames <= 0)
                break;
        }

        ret2.ThrowOnFailure();

        videoEncoder.Drain().ThrowOnFailure();

        while (true)
        {
            ret = videoEncoder.RecvPacket(packet);

            if (ret.Success)
            {
                packet.RescaleTimestamp(videoEncoder.Timebase, videoStreamMux.Timebase, videoStreamMux.Index);
                muxer.WritePacketInterleaved(packet);
            }
            else if (!ret.TryAgain && !ret.Eof)
                ret.ThrowOnFailure();
            else
                break;
        }

        muxer.WriteTrailer();
        muxer.Dispose();
        videoEncoder.Dispose();
        packet.Dispose();
        videoFrame.Dispose();
        imageFrame.Dispose();
        overlayFrames.Dispose();
    }

    static VideoFrame GetImage(string path)
    {
        Demuxer demuxer = new();
        demuxer.Open(path);
        VideoStream videoStream = demuxer.BestVideoStream() ?? throw new Exception("No Video");
        demuxer.Enable(videoStream);

        VideoDecoder videoDecoder = new(CodecSpec.FindVideoDecoder(videoStream.CodecId) ?? throw new Exception("No codec"), videoStream);
        videoDecoder.Open();

        AVPacket*   packet  = av_packet_alloc();
        AVFrame*    frame   = av_frame_alloc();
        FFmpegResult ret;

        while ((ret = demuxer.ReadPacket(packet)).Success)
        {
            videoDecoder.SendPacket(packet).ThrowOnFailure();
            ret = videoDecoder.RecvFrame(frame);
            if (ret.Success)
            {
                demuxer.Dispose();
                videoDecoder.Dispose();
                av_packet_free(&packet);
                return new(frame);
            }
            else if (!ret.TryAgain)
                break;
        }

        if (ret.Eof)
        {
            videoDecoder.Drain().ThrowOnFailure();
            if (videoDecoder.RecvFrame(frame).Success)
            {
                demuxer.Dispose();
                videoDecoder.Dispose();
                av_packet_free(&packet);
                return new(frame);
            }
        }

        throw new Exception("Decoding failed");
    }

    class OverlayFrames
    {
        public VideoFilterLink SinkConfiguration { get; private set; }

        FilterGraph         filterGraph;
        VideoBufferSource   srcDDA, srcOverlay;
        FilterContext       overlay;
        VideoBufferSink     sink;
        VideoFrame          overlayFrame;
        DDAFrames           ddaFrames;
        int                 frameNum;

        public OverlayFrames(VideoFrame overlayFrame, int framerate = 25)
        {
            filterGraph         = new();
            //filterGraph.ImageConvOpts = "sws_flags=lanczos+accurate_rnd+full_chroma_int+full_chroma_inp+bitexact";
            filterGraph.ImageConvOpts = "sws_flags=full_chroma_int";
            ddaFrames           = new(framerate);
            this.overlayFrame   = overlayFrame;

            srcDDA = new(filterGraph, new() 
            {
                PixelFormat         = ddaFrames.SinkConfiguration.PixelFormat, 
                Width               = ddaFrames.SinkConfiguration.Width, 
                Height              = ddaFrames.SinkConfiguration.Height,
                ColorRange          = ddaFrames.SinkConfiguration.ColorRange,
                ColorSpace          = ddaFrames.SinkConfiguration.ColorSpace,
                SampleAspectRatio   = ddaFrames.SinkConfiguration.SampleAspectRatio,
                Timebase            = new(1, framerate),
                FrameRate           = new(framerate, 1)
            }, "src_0");

            srcOverlay = new(filterGraph, new() 
            {
                PixelFormat         = overlayFrame.PixelFormat, 
                Width               = overlayFrame.Width, 
                Height              = overlayFrame.Height,
                ColorRange          = overlayFrame.ColorRange,
                ColorSpace          = overlayFrame.ColorSpace,
                SampleAspectRatio   = overlayFrame.SampleAspectRatio,
                Timebase            = new(1, framerate),
                FrameRate           = new(framerate, 1)
            }, "src_1");

            overlay = new(filterGraph, "overlay", "overlay_0", "format=gbrp:x=0:y=0:repeatlast=1");
        
            sink = new(filterGraph, new()
            {
                PixelFormats    = [AVPixelFormat.Rgb24]
            }, "sink_0");

            srcDDA.Link(overlay).Link(sink);
            srcOverlay.Link(overlay.InPads[1]);
        
            filterGraph.Config().ThrowOnFailure();
            //Console.WriteLine(filterGraph.Dump());
        
            SinkConfiguration = (VideoFilterLink) sink.InPads[0].FilterLink!;
        }

        public FFmpegResult RecvFrame(VideoFrame videoFrame)
        {
            ddaFrames.RecvFrame(videoFrame).ThrowOnFailure();
            
            videoFrame.Pts      = overlayFrame.Pts      = frameNum++;
            videoFrame.Duration = overlayFrame.Duration = 1;

            srcDDA.SendFrame(videoFrame, AVBuffersrcFlag.KeepRef).ThrowOnFailure();
            srcOverlay.SendFrame(overlayFrame, AVBuffersrcFlag.KeepRef).ThrowOnFailure();
            
            return sink.RecvFrame(videoFrame);
        }

        public void Dispose()
        {
            filterGraph.Dispose();
            ddaFrames.Dispose();
        }
    }

    class DDAFrames
    {
        public VideoFilterLink SinkConfiguration { get; private set; }

        FilterGraph     filterGraph;
        FilterContext   ddagrab;
        FilterContext   hwdownload;
        VideoBufferSink sink;

        public DDAFrames(int framerate = 25)
        {
            filterGraph     = new();
            ddagrab         = new(filterGraph, "ddagrab", "ddagraph_0", $"framerate={framerate}");
            hwdownload      = new(filterGraph, "hwdownload", "hwdownload_0");
            sink = new(filterGraph, new()
            {
                PixelFormats = [AVPixelFormat.Bgra]
            }, "sink_0");

            ddagrab.Link(hwdownload).Link(sink);
            filterGraph.Config().ThrowOnFailure();

            SinkConfiguration = (VideoFilterLink) sink.InPads[0].FilterLink!;
        }

        public FFmpegResult RecvFrame(VideoFrame videoFrame)
            => sink.RecvFrame(videoFrame);

        public void Dispose()
            => filterGraph.Dispose();
    }
}