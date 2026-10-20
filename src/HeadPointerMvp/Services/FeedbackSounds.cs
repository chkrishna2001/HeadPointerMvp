using NAudio.Wave;
using HeadPointerMvp.Infrastructure;

namespace HeadPointerMvp.Services;

/// <summary>
/// Three sounds with clearly different character (not three beeps): a rising "whoop" when the wake word is heard,
/// a short dry "tick" when a command ran, and Windows' own speech-error sound when no command followed.
/// </summary>
public static class FeedbackSounds
{
    private const int Rate = 44100;
    private static readonly string MissFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Speech Misrecognition.wav");
    private static readonly float[] Whoop = Sweep(380, 900, 0.14, 0.22);
    private static readonly float[] Tick = Tick1();

    public static void Play(string kind, FileLogger logger)
    {
        try
        {
            switch (kind)
            {
                case "wake":
                    PlaySamples(Whoop);
                    break;
                case "ok":
                    PlaySamples(Tick);
                    break;
                case "miss" when File.Exists(MissFile):
                    using (var reader = new AudioFileReader(MissFile))
                    {
                        reader.Volume = 0.6f;
                        Play(reader);
                    }

                    break;
                case "miss":
                    PlaySamples(Sweep(500, 180, 0.30, 0.25)); // falling, the opposite of the wake sound
                    break;
            }
        }
        catch (Exception ex)
        {
            _ = logger.InfoAsync($"[voice] feedback sound failed: {ex.Message}");
        }
    }

    private static void PlaySamples(float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        using var stream = new RawSourceWaveStream(bytes, 0, bytes.Length, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1));
        Play(stream);
    }

    private static void Play(IWaveProvider source)
    {
        using var output = new WaveOutEvent();
        output.Init(source);
        output.Play();
        while (output.PlaybackState == PlaybackState.Playing) Thread.Sleep(10);
    }

    /// <summary>Pitch glide from one frequency to another with a soft attack and release.</summary>
    private static float[] Sweep(double fromHz, double toHz, double seconds, double gain)
    {
        var count = (int)(Rate * seconds);
        var samples = new float[count];
        double phase = 0;
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)count;
            phase += 2 * Math.PI * (fromHz + ((toHz - fromHz) * t)) / Rate;
            var envelope = Math.Min(1, t * 12) * Math.Min(1, (1 - t) * 6);
            samples[i] = (float)(Math.Sin(phase) * envelope * gain);
        }

        return samples;
    }

    /// <summary>A dry, woody click: a noise burst plus a short low thump, gone in about 50 ms.</summary>
    private static float[] Tick1()
    {
        var count = (int)(Rate * 0.05);
        var samples = new float[count];
        var random = new Random(7);
        for (var i = 0; i < count; i++)
        {
            var decay = Math.Exp(-i / (Rate * 0.008));
            var noise = (random.NextDouble() * 2) - 1;
            var thump = Math.Sin(2 * Math.PI * 140 * i / Rate);
            samples[i] = (float)(((noise * 0.6) + (thump * 0.8)) * decay * 0.5);
        }

        return samples;
    }
}
