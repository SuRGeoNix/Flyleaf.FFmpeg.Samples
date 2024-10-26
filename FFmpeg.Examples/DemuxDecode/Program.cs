using Flyleaf.FFmpeg.Format;
using Flyleaf.FFmpeg.Format.Demux;
using Flyleaf.FFmpeg;
using Flyleaf.FFmpeg.Spec;
using Flyleaf.FFmpeg.Codec.Decode;
using Flyleaf.FFmpeg.Codec;

using static Flyleaf.FFmpeg.Raw;
using static Common.Utils;

LoadFFmpeg();

DemuxDecodeSample.Run(new()
{
    InputFile       = Sample,
    AudioOutFile    = RAWAudio,
    VideoOutFile    = RAWVideo,
});

public unsafe class DemuxDecodeSample
{
    public static void Run(DemuxDecodeOptions opt)
        => _ = new DemuxDecodeSample(opt);

    public class DemuxDecodeOptions
    {
        public required string  InputFile       { get; init; } = null!;
        public required string  AudioOutFile    { get; init; } = null!;
        public required string  VideoOutFile    { get; init; } = null!;
        public long             MaxFrames       { get; init; } = 100;
    }

    DemuxDecodeSample(DemuxDecodeOptions opt)
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
        Packet              packet      = new();
        long                maxFrames   = opt.MaxFrames;
        #endregion

        #region Find Video Stream / Enable Video Stream / Find Video Decoder / Open Video Decoder / Prepare Output
        VideoStream         videoStream = demuxer.BestVideoStream() ?? throw new Exception("Could not find video stream");
        demuxer.Enable(videoStream);

        VideoDecoderSpec    videoCodec  = CodecSpec.FindVideoDecoder(videoStream.CodecId) ?? throw new Exception($"Could not find codec for '{videoStream.CodecId}' id");
        VideoDecoder        videoDecoder= new(videoCodec, videoStream);
        videoDecoder.Open().ThrowOnFailure();
        
        VideoFrame          videoFrame  = new();
        FileStream          videoFile   = new(opt.VideoOutFile, FileMode.Create, FileAccess.Write);
        #endregion

        #region Find Audio Stream / Enable Audio Stream / Find Audio Decoder / Open Audio Decoder / Prepare Output
        AudioStream         audioStream = demuxer.BestAudioStream() ?? throw new Exception("Could not find audio stream");
        demuxer.Enable(audioStream);

        AudioDecoderSpec    audioCodec  = CodecSpec.FindAudioDecoder(audioStream.CodecId) ?? throw new Exception($"Could not find codec for '{audioStream.CodecId}' id");
        AudioDecoder        audioDecoder= new(audioCodec, audioStream);
        audioDecoder.Open().ThrowOnFailure();
        
        AudioFrame          audioFrame  = new();
        FileStream          audioFile   = new(opt.AudioOutFile, FileMode.Create, FileAccess.Write);
        int                 bytesPerSample = audioDecoder.SampleFormat.GetBytesPerSample();;
        #endregion

        #region Demux Packet / Decode Frame / Write Raw Frame
        FFmpegResult ret;
        while (demuxer.ReadPacket(packet).Success && maxFrames > 0) // No draining in this sample
        {
            if (packet.StreamIndex == videoStream.Index)
            {
                videoDecoder.SendPacket(packet).ThrowOnFailure();
                
                while(true)
                {
                    ret = videoDecoder.RecvFrame(videoFrame);

                    if (ret.Success)
                    {
                        // TBR: this does not require copying the data but it might not work in some cases (eg. codec padding / cropping)
                        //for (int i = 0; i < planeSizes.Count; i++)
                        //    videoFile.Write(new ReadOnlySpan<byte>((void*)videoFrame.Data[i], (int)planeSizes[i]));

                        byte[] frameBytes = videoFrame.ToRawImage();
                        videoFile.Write(frameBytes, 0, frameBytes.Length);

                        maxFrames--;
                    }
                    else if (!ret.TryAgain && !ret.Eof)
                        ret.ThrowOnFailure();
                    else
                        break;
                }
            }
            else if (packet.StreamIndex == audioStream.Index)
            {
                audioDecoder.SendPacket(packet).ThrowOnFailure();

                while(true)
                {
                    ret = audioDecoder.RecvFrame(audioFrame);

                    if (ret.Success)
                        audioFile.Write(new ReadOnlySpan<byte>((void*)audioFrame.Data[0], audioFrame.Samples * bytesPerSample));
                    else if (!ret.TryAgain && !ret.Eof)
                        ret.ThrowOnFailure();
                    else
                        break;
                }
            }
        }
        #endregion

        Console.WriteLine($"Open raw video with Flyleaf -> fmt://rawvideo?{opt.VideoOutFile}&pixel_format={videoDecoder.PixelFormat.GetName()}&video_size={videoDecoder.Width}x{videoDecoder.Height}&framerate={videoDecoder.FrameRate}");

        int channels;
        AVSampleFormat format = audioDecoder.SampleFormat;
        
        if (format.IsPlanar())
        {
            Console.WriteLine($"Warning: the sample format the decoder produced is planar {format.GetName()}.This example will output the first channel only.");
            format = format.GetPackedFormat();
            channels = 1;
        }
        else
            channels = audioDecoder.ChannelLayout.nb_channels;
        
        string demuxerName = format switch
        {
            AVSampleFormat.U8 => "u8",
            AVSampleFormat.S16 => BitConverter.IsLittleEndian ? "s16le" : "s16be",
            AVSampleFormat.S32 => BitConverter.IsLittleEndian ? "s32le" : "s32be",
            AVSampleFormat.Flt => BitConverter.IsLittleEndian ? "f32le" : "f32be",
            AVSampleFormat.Dbl => BitConverter.IsLittleEndian ? "f64be" : "f64be",
            _ => "<unknown>"
        };

        Console.WriteLine($"Open raw audio with Flyleaf -> fmt://{demuxerName}?{opt.AudioOutFile}&channels={channels}&sample_rate={audioDecoder.SampleRate}");

        #region Dispose
        demuxer.Dispose();
        packet.Dispose();
        videoDecoder.Dispose();
        audioDecoder.Dispose();
        videoFrame.Dispose();
        audioFrame.Dispose();
        videoFile.Dispose();
        audioFile.Dispose();
        #endregion
    }

    //string GetDemuxerName(AVSampleFormat format)
    //{
        

    //}
}