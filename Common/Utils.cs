using Flyleaf.FFmpeg;
using Flyleaf.FFmpeg.Format.Demux;
using static Flyleaf.FFmpeg.Utils;

namespace Common;

public static class Utils
{
    // FFmpeg x86/x64 Paths
    public static string FFmpegPath_x86 = FindFolderBelow("FFmpeg/x86")!;
    public static string FFmpegPath_x64 = FindFolderBelow("FFmpeg/x64")!;

    // IN Path / Files
    public static string Sample = FindFileBelow("Sample.mp4")!;
    public const string Bluray  = @$"bluray:d:";

    //public const string InDir   =@"C:\VideoSamples\";
    //public const string B0      = @$"{InDir}0.mp4";

    public const string Coca    = $@"https://upload.wikimedia.org/wikipedia/commons/thumb/c/ce/Coca-Cola_logo.svg/512px-Coca-Cola_logo.svg.png";

    // OUT Path / Files
    public const string OutDir  =@"";
    public const string OutName = "test";

    public const string UDP     = @$"udp://localhost:5000/{OutName}.ts";

    public const string AVI     = $@"{OutDir}{OutName}.avi";
    public const string MP4     = $@"{OutDir}{OutName}.mp4";
    public const string MKV     = $@"{OutDir}{OutName}.mkv";
    public const string TS      = $@"{OutDir}{OutName}.ts";

    public const string RAWAudio= $@"{OutDir}{OutName}.audio.raw";
    public const string RAWVideo= $@"{OutDir}{OutName}.video.raw";

    public static void LoadFFmpeg()
    {
        LoadLibraries(Environment.Is64BitProcess ? FFmpegPath_x64 : FFmpegPath_x86, LoadProfile.All);
        FFmpegLog.SetLogLevel(LogLevel.Verb);
    }

    public static void FFProbe(string file)
    {
        var demuxer = new Demuxer()
        {
            MaxProbeBytes   = 500 * 1024 * 1024,
            MaxAnalyzeMcs   = (long)TimeSpan.FromSeconds(500).TotalMicroseconds
        };
        demuxer.Open(file).ThrowOnFailure();
        demuxer.Analyse();
        demuxer.Dump(file);
        demuxer.Dispose();
    }

    public static string? FindFileBelow(string filename)
    {
        string? current = AppDomain.CurrentDomain.BaseDirectory;

        while (current != null)
        {
            if (File.Exists(Path.Combine(current, filename)))
                return Path.Combine(current, filename);

            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }

    public static string? FindFolderBelow(string folder)
    {
        string? current = AppDomain.CurrentDomain.BaseDirectory;

        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current, folder)))
                return Path.Combine(current, folder);

            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }
}
