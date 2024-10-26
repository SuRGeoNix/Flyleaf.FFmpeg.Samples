using Flyleaf.FFmpeg;
using Flyleaf.FFmpeg.Codec;
using Flyleaf.FFmpeg.Codec.Decode;
using Flyleaf.FFmpeg.HWAccel;
using Flyleaf.FFmpeg.Format;
using Flyleaf.FFmpeg.Format.Demux;
using Flyleaf.FFmpeg.Spec;

using static Flyleaf.FFmpeg.Raw;
using static Common.Utils;

LoadFFmpeg();

HWDecodeSample.Run(new()
{
    InputFile   = Sample,
    OutputFile  = RAWVideo,
    HWDevice    = AVHWDeviceType.D3d12va
});

public unsafe class HWDecodeSample
{
    public static void Run(HWDecodeOptions opt)
        => _ = new HWDecodeSample(opt);

    public class HWDecodeOptions
    {
        public required string  InputFile       { get; init; } = null!;
        public required string  OutputFile      { get; init; } = null!;
        public required AVHWDeviceType
                                HWDevice        { get; init; } = AVHWDeviceType.None;
        public long             MaxFrames       { get; init; } = 100;
    }

    HWDecodeSample(HWDecodeOptions opt)
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
        
        #region Find Video Stream / Enable Video Stream / Initialize HW Device / Find HW Video Decoder / Open HW Video Decoder / Prepare Output
        VideoStream         videoStream     = demuxer.BestVideoStream() ?? throw new Exception("Could not find video stream");
        demuxer.Enable(videoStream);
        
        HWFramesContext?    hwFrames        = null;
        HWDeviceContext     hwDevice        = new(opt.HWDevice, null /* TBR: to force the right constructor*/);
        AVPixelFormat       hwPixelFormat   = HWDeviceSpec.FindHWPixelFormat(opt.HWDevice);
        VideoDecoderSpec    videoCodec      = CodecSpec.FindHWVideoDecoder(videoStream.CodecId, hwPixelFormat) ?? throw new Exception($"Could not find hw decoder for '{videoStream.CodecId}' id and {hwPixelFormat} pixel format");
        VideoDecoder?       videoDecoder    = null; // TBR: event with opaque for GetFormat?
        videoDecoder = new(videoCodec, videoStream, GetHWFormat) // GetHWFormat required to set HWFramesContext
        {
            HWDeviceContext = hwDevice, // required before opening
        };
        videoDecoder.Open().ThrowOnFailure();
        
        VideoFrame          videoFrame      = new();
        VideoFrame          swVideoFrame    = new();
        FileStream          videoFile       = new(opt.OutputFile, FileMode.Create, FileAccess.Write);
        #endregion

        #region Initialize HW Frames and Choose HW Pixel Format (Local function to access local resources without opaques)
        AVPixelFormat GetHWFormat(AVCodecContext* s, AVPixelFormat* fmt)
        {
            List<AVPixelFormat> availablePixelFormats = ArrayUtils.GetPixelFormats(fmt);
            if (!availablePixelFormats.Contains(hwPixelFormat))
                throw new Exception($"HW decoding is not supported for {hwPixelFormat} pixel format");

            if (hwFrames == null)
            {
                hwFrames = new(videoDecoder!, hwDevice, hwPixelFormat);
                hwFrames.InitFrames().ThrowOnFailure();
            }

            videoDecoder!.HWFramesContext = hwFrames;

            return hwPixelFormat;
        }
        #endregion

        #region Demux Packet / HW Decode Frame / HW Download to SW / Write Raw SW Frame
        FFmpegResult ret;
        while (demuxer.ReadPacket(packet).Success && maxFrames > 0)
        {
            if (packet.StreamIndex == videoStream.Index)
            {
                videoDecoder.SendPacket(packet).ThrowOnFailure();
                
                while(true)
                {
                    ret = videoDecoder.RecvFrame(videoFrame);

                    if (ret.Success)
                    {
                        if (videoFrame.PixelFormat != hwPixelFormat)
                            throw new Exception($"HW decoding is not supported for {hwPixelFormat} pixel format. The decoder falled back to SW pixel format {videoFrame.PixelFormat}");

                        videoFrame.TransferTo(swVideoFrame).ThrowOnFailure();

                        //VideoFrame vfs = new(swVideoFrame.Width, swVideoFrame.Height, AVPixelFormat.Nv12);
                        //VideoFrame vfs2 = new(swVideoFrame.Width, swVideoFrame.Height, AVPixelFormat.Nv12);

                        //av_image_copy((byte**)&vfs.frame->data, (int*)&vfs.frame->linesize, (byte**)&swVideoFrame.frame->data, (int*)&swVideoFrame.frame->linesize, swVideoFrame.PixelFormat, swVideoFrame.Width, swVideoFrame.Height);
                        //// this is slower as it requires explicit from array8 to array4 (however both will pass by ref/pointer, and not as value) ** diff with copy2, the simple copy uses an extra vzeroupper before call
                        //av_image_copy2((byte_ptrArray4)vfs2.AVFrame->data, (int_array4)vfs2.AVFrame->linesize, (byte_ptrArray4)swVideoFrame.AVFrame->data, (int_array4)swVideoFrame.AVFrame->linesize, swVideoFrame.PixelFormat, swVideoFrame.Width, swVideoFrame.Height);

                        // restore
                        //VideoFrame vfs = new(swVideoFrame.Width, swVideoFrame.Height, AVPixelFormat.Rgba);
                        //ImageConverter sws = new(swVideoFrame.PixelFormat, swVideoFrame.Width, swVideoFrame.Height, AVPixelFormat.Rgba, swVideoFrame.Width, swVideoFrame.Height, SwsFlags.AccurateRnd | SwsFlags.Bitexact | SwsFlags.Lanczos | SwsFlags.FullChrHInt | SwsFlags.FullChrHInp);
                        //sws.Convert(swVideoFrame, vfs);
                        
                        byte[] frameBytes = swVideoFrame.ToRawImage();
                        videoFile.Write(frameBytes, 0, frameBytes.Length);

                        swVideoFrame.UnRef(); // might not required either?*
                        //vfs.Dispose();
                        //sws.Dispose();
                        maxFrames--;
                    }
                    else if (!ret.TryAgain && !ret.Eof)
                        ret.ThrowOnFailure();
                    else
                        break;
                }
            }
        }
        #endregion

        Console.WriteLine($"Open with Flyleaf -> fmt://rawvideo?{opt.OutputFile}&pixel_format={hwFrames!.SWPixelFormat.GetName()}&video_size={videoStream.Width}x{videoStream.Height}");

        #region Dispose
        demuxer.Dispose();
        packet.Dispose();
        videoDecoder.Dispose();
        videoFile.Dispose();
        videoFrame.Dispose();
        swVideoFrame.Dispose();
        hwDevice.Dispose();
        hwFrames?.Dispose();
        #endregion
    }
}