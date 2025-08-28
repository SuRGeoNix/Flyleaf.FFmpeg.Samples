using Flyleaf.FFmpeg.Format;
using Flyleaf.FFmpeg.Format.Demux;
using Flyleaf.FFmpeg;
using Flyleaf.FFmpeg.Spec;

using Flyleaf.FFmpeg.Codec.Decode;
using Flyleaf.FFmpeg.Codec;
using Flyleaf.FFmpeg.Filter;

using static Common.Utils;

LoadFFmpeg();

DecodeFilterAudioSample.Run(new()
{
    InputFile   = Sample,
    OutputFile  = RAWAudio
});

public unsafe class DecodeFilterAudioSample
{
    public static void Run(DecodeFilterAudioOptions opt)
        => _ = new DecodeFilterAudioSample(opt);

    public class DecodeFilterAudioOptions
    {
        public required string  InputFile       { get; init; } = null!;
        public required string  OutputFile      { get; init; } = null!;
        public long             MaxFrames       { get; init; } = 100;
    }

    DecodeFilterAudioSample(DecodeFilterAudioOptions opt)
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

        #region Find Audio Stream / Enable Audio Stream / Find Audio Decoder / Open Audio Decoder / Prepare Output
        AudioStream         audioStream = demuxer.BestAudioStream() ?? throw new Exception("Could not find audio stream");
        demuxer.Enable(audioStream);

        AudioDecoderSpec    audioCodec  = CodecSpec.FindAudioDecoder(audioStream.CodecId) ?? throw new Exception($"Could not find codec for '{audioStream.CodecId}' id");
        AudioDecoder        audioDecoder= new(audioCodec, audioStream);
        audioDecoder.Open().ThrowOnFailure();
        
        AudioFrame          audioFrame  = new();
        FileStream          audioFile   = new(opt.OutputFile, FileMode.Create, FileAccess.Write);
        #endregion

        #region Filter
        FilterGraph         filterGraph     = new();
        AudioBufferSource   audioBufferSrc  = new(filterGraph, new(audioDecoder));
        AudioBufferSink     audioBufferSink = new(filterGraph, new()
        {
            ChannelLayouts  = ["mono"],
            SampleFormats   = [AVSampleFormat.S16],
            SampleRates     = [8000]
        });
        filterGraph.Parse("aresample=8000,aformat=sample_fmts=s16:channel_layouts=mono", out var inputs, out var outputs).ThrowOnFailure();

        audioBufferSrc.Link(inputs[0]);
        outputs[0].Link(audioBufferSink);

        filterGraph.Config().ThrowOnFailure();
        AudioFrame audioFiltFrame = new();

        AudioFilterLink sinkLink = (AudioFilterLink)audioBufferSink.InPads[0].FilterLink!;
        var bytesPerSample = sinkLink.SampleFormat.GetBytesPerSample();
        #endregion

        #region Demux Packet / Decode Frame / Write Raw Frame
        FFmpegResult ret;
        while (demuxer.ReadPacket(packet).Success && maxFrames > 0) // No draining in this sample
        {
            if (packet.StreamIndex != audioStream.Index)
                continue;

            audioDecoder.SendPacket(packet).ThrowOnFailure();
                
            while(true)
            {
                ret = audioDecoder.RecvFrame(audioFrame);

                if (ret.Success)
                {
                    audioBufferSrc.SendFrame(audioFrame).ThrowOnFailure();

                    while (true)
                    {
                        ret = audioBufferSink.RecvFrame(audioFiltFrame);

                        if (ret.Success)
                        {
                            audioFile.Write(new ReadOnlySpan<byte>((void*)audioFiltFrame.Data[0], audioFiltFrame.Samples * bytesPerSample * audioFiltFrame.ChannelLayout.nb_channels));
                            maxFrames--;
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

        Console.WriteLine($"Open with Flyleaf ->\r\nfmt://s16le?{audioFile.Name}&ch_layout=mono&sample_rate=8000");
        
        #region Dispose
        demuxer.Dispose();
        audioDecoder.Dispose();
        packet.Dispose();
        audioFrame.Dispose();
        audioFiltFrame.Dispose();
        filterGraph.Dispose();
        audioFile.Dispose();
        #endregion
    }
}