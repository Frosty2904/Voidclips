using System.IO;
using NAudio.Lame;
using NAudio.Wave;
using OggVorbisEncoder;

namespace VoidClip.Audio;

// ══════════════════════════════════════════════════════════════
//  WAV — the on-disk working format (48 kHz stereo, 16-bit PCM)
// ══════════════════════════════════════════════════════════════
public static class WavIO
{
    public static void Write(string path, float[] canonical)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var fmt = new WaveFormat(Fmt.Rate, 16, Fmt.Channels);
        using var w = new WaveFileWriter(path, fmt);
        var bytes = ToPcm16(canonical);
        w.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Read any supported file and return canonical 48 kHz stereo float.</summary>
    public static float[] Read(string path)
    {
        using var reader = new AudioFileReader(path);
        var wf = reader.WaveFormat;
        var list = new List<float>((int)Math.Min(int.MaxValue, reader.Length / 2));
        var buf = new float[16384];
        int read;
        while ((read = reader.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < read; i++) list.Add(buf[i]);

        var raw = list.ToArray();
        return Dsp.ToCanonical(raw, wf.SampleRate, wf.Channels);
    }

    public static byte[] ToPcm16(float[] data)
    {
        var bytes = new byte[data.Length * 2];
        for (int i = 0; i < data.Length; i++)
        {
            var v = (short)(Math.Clamp(data[i], -1f, 1f) * 32767f);
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return bytes;
    }

    public static double Duration(float[] canonical)
        => canonical.Length / (double)(Fmt.Rate * Fmt.Channels);
}

// ══════════════════════════════════════════════════════════════
//  EXPORT
// ══════════════════════════════════════════════════════════════
public static class Exporter
{
    public static void ToWav(string path, float[] canonical) => WavIO.Write(path, canonical);

    public static void ToMp3(string path, float[] canonical, int bitrateKbps)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var fmt = new WaveFormat(Fmt.Rate, 16, Fmt.Channels);
        using var w = new LameMP3FileWriter(path, fmt, bitrateKbps);
        var bytes = WavIO.ToPcm16(canonical);
        w.Write(bytes, 0, bytes.Length);
        w.Flush();
    }

    /// <summary>Vorbis VBR. <paramref name="quality"/> is 0..1 (roughly -q0 … -q10).</summary>
    public static void ToOgg(string path, float[] canonical, float quality)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        const int channels = Fmt.Channels;
        var info = VorbisInfo.InitVariableBitRate(channels, Fmt.Rate, Math.Clamp(quality, 0.05f, 1.0f));
        var oggStream = new OggStream(Random.Shared.Next(int.MaxValue));

        var comments = new Comments();
        comments.AddTag("ENCODER", "VoidClip");

        oggStream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
        oggStream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(comments));
        oggStream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));

        using var fs = File.Create(path);

        // headers must land in their own pages before any audio
        while (oggStream.PageOut(out OggPage page, true)) WritePage(fs, page);

        var state = ProcessingState.Create(info);
        var frames = canonical.Length / channels;

        // de-interleave once; clips are short enough that this is cheap
        var planar = new float[channels][];
        for (int c = 0; c < channels; c++) planar[c] = new float[frames];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels; c++)
                planar[c][f] = canonical[f * channels + c];

        const int block = 1024;
        var done = false;
        for (int offset = 0; offset < frames && !done; offset += block)
        {
            var n = Math.Min(block, frames - offset);
            state.WriteData(planar, n, offset);
            done = Drain(state, oggStream, fs);
        }

        if (!done)
        {
            state.WriteEndOfStream();
            Drain(state, oggStream, fs);
        }

        while (oggStream.PageOut(out OggPage page, true)) WritePage(fs, page);
    }

    private static bool Drain(ProcessingState state, OggStream oggStream, Stream fs)
    {
        while (!oggStream.Finished && state.PacketOut(out OggPacket packet))
        {
            oggStream.PacketIn(packet);
            while (!oggStream.Finished && oggStream.PageOut(out OggPage page, false))
                WritePage(fs, page);
        }
        return oggStream.Finished;
    }

    private static void WritePage(Stream fs, OggPage page)
    {
        fs.Write(page.Header, 0, page.Header.Length);
        fs.Write(page.Body, 0, page.Body.Length);
    }

    public static string Extension(Models.ExportFormat f) => f switch
    {
        Models.ExportFormat.Mp3 => ".mp3",
        Models.ExportFormat.Ogg => ".ogg",
        _ => ".wav"
    };

    public static void Export(string path, float[] canonical, Models.ExportFormat format, int mp3Bitrate, float oggQuality)
    {
        switch (format)
        {
            case Models.ExportFormat.Mp3: ToMp3(path, canonical, mp3Bitrate); break;
            case Models.ExportFormat.Ogg: ToOgg(path, canonical, oggQuality); break;
            default: ToWav(path, canonical); break;
        }
    }
}
