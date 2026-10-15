using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using HeadPointerMvp.Infrastructure;

namespace HeadPointerMvp.Services;

/// <summary>
/// Offline monitor-switching by voice, using Windows' built-in speech recognition (no cloud, no
/// network calls). Deliberately uses a tiny fixed grammar ("m one", "monitor two", ...) instead of
/// free dictation: it's far lighter on CPU, and short command phrases are unlikely to be said by
/// accident during normal conversation the way a bare number would be.
/// </summary>
public sealed class VoiceCommandService : IDisposable
{
    private const float MinimumConfidence = 0.4f;
    private const string MonitorIndexKey = "monitorIndex";
    private const int TargetSampleRate = 16000;
    private static readonly string[] TriggerWords = ["m", "monitor"];
    // 1-indexed by design: NumberWords[i] (spoken "one", "two", ...) maps to monitor index i, so
    // "monitor one" means "my first monitor" the way a person would actually say it.
    private static readonly string[] NumberWords =
        ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];

    private const string GridDigitKey = "gridDigit";
    private const string GridCommandKey = "gridCommand";
    private const string OpenGridKey = "openGrid";

    private readonly FileLogger _logger;
    private Grammar? _gridGrammar;
    private SpeechRecognitionEngine? _engine;
    private WasapiCapture? _capture;
    private LiveAudioStream? _liveStream;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;

    public VoiceCommandService(FileLogger logger) => _logger = logger;

    public event Action<int>? MonitorRequested;

    /// <summary>"grid" / "mouse grid" / "show grid".</summary>
    public event Action? GridRequested;

    /// <summary>A digit 1..9 spoken while the grid is open.</summary>
    public event Action<int>? GridDigitSpoken;

    public event Action<GridCommand>? GridCommandSpoken;

    /// <summary>The digit/click grammar is only live while the grid is open, so everyday speech can't trip it.</summary>
    public void SetGridMode(bool active)
    {
        if (_gridGrammar is not null) _gridGrammar.Enabled = active;
    }

    /// <summary>Lists active capture (recording) devices for the Settings microphone picker.</summary>
    public static IReadOnlyList<(string Id, string Name)> ListMicrophones()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var result = new List<(string, string)>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                result.Add((device.ID, device.FriendlyName));
                device.Dispose();
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <param name="microphoneDeviceId">Empty/null follows Windows' system default recording device. Otherwise a WASAPI device ID from <see cref="ListMicrophones"/>.</param>
    public void Start(int monitorCount, string? microphoneDeviceId = null)
    {
        if (monitorCount <= 0 || _engine is not null) return;

        try
        {
            // System.Speech's .NET (non-Framework) port has a long-standing bug: its internal SAPI
            // interop code formats some strings assuming invariant/en-US number formatting (comma vs.
            // period decimal separators, etc). On a system whose current culture isn't en-US, that
            // throws "FormatException: ')' is not a valid format specifier" during setup or from the
            // engine's own background recognition thread. This doesn't affect which language the
            // recognizer itself understands (still whatever's installed) -- only the ambient .NET
            // culture System.Speech's plumbing formats strings with -- so forcing invariant culture
            // here is safe and is the standard workaround for this exact symptom.
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

            // Each number word carries its monitor index as a semantic value baked into the grammar
            // itself, rather than being re-derived later by splitting/parsing the recognized text and
            // matching it back against NumberWords by position.
            var count = Math.Min(monitorCount, NumberWords.Length);
            var numberAlternatives = new GrammarBuilder[count];
            for (var i = 0; i < count; i++) numberAlternatives[i] = new SemanticResultValue(NumberWords[i], i).ToGrammarBuilder();

            var builder = new GrammarBuilder();
            builder.Append(new Choices(TriggerWords));
            builder.Append(new SemanticResultKey(MonitorIndexKey, new Choices(numberAlternatives)));

            _engine = new SpeechRecognitionEngine();

            // SAPI applies its own internal confidence gate before SpeechRecognized/Rejected even
            // fires, on top of our own MinimumConfidence check below -- two opaque thresholds stacked
            // is exactly why quieter speech got nothing at all. Drop SAPI's to let everything through
            // to the one threshold we can see and tune (the logged confidence values above).
            _engine.UpdateRecognizerSetting("CFGConfidenceRejectionThreshold", 0);

            _engine.LoadGrammar(new Grammar(builder));
            _engine.LoadGrammar(BuildOpenGridGrammar());
            _gridGrammar = BuildGridGrammar();
            _gridGrammar.Enabled = false;
            _engine.LoadGrammar(_gridGrammar);
            _engine.SpeechRecognized += OnSpeechRecognized;
            _engine.SpeechRecognitionRejected += OnSpeechRejected;

            if (string.IsNullOrEmpty(microphoneDeviceId))
            {
                _engine.SetInputToDefaultAudioDevice();
            }
            else
            {
                StartCaptureFromDevice(microphoneDeviceId);
                var format = new SpeechAudioFormatInfo(EncodingFormat.Pcm, TargetSampleRate, 16, 1, TargetSampleRate * 2, 2, null);
                _engine.SetInputToAudioStream(_liveStream, format);
            }

            _engine.RecognizeAsync(RecognizeMode.Multiple);
            _ = _logger.InfoAsync($"Voice commands listening for \"m\"/\"monitor\" + one..{NumberWords[count - 1]} ({count} monitor(s), 1-indexed)"
                + (string.IsNullOrEmpty(microphoneDeviceId) ? ", system default microphone." : $", device {microphoneDeviceId}."));
        }
        catch (Exception ex)
        {
            _ = _logger.InfoAsync($"Voice commands unavailable: {ex.Message}");
            StopCapture();
            _engine?.Dispose();
            _engine = null;
        }
    }

    private static Grammar BuildOpenGridGrammar()
    {
        var phrases = new Choices("grid", "mouse grid", "show grid", "pointer grid");
        return new Grammar(new GrammarBuilder(new SemanticResultKey(OpenGridKey, new SemanticResultValue(phrases, "open")))) { Name = "openGrid" };
    }

    private static Grammar BuildGridGrammar()
    {
        var digits = new Choices();
        for (var i = 0; i < 9; i++) digits.Add(new SemanticResultValue(NumberWords[i], i + 1));

        var commands = new Choices();
        void Add(string phrase, GridCommand command) => commands.Add(new SemanticResultValue(phrase, (int)command));
        Add("click", GridCommand.Click);
        Add("select", GridCommand.Click);
        Add("right click", GridCommand.RightClick);
        Add("double click", GridCommand.DoubleClick);
        Add("back", GridCommand.Back);
        Add("undo", GridCommand.Back);
        Add("cancel", GridCommand.Cancel);
        Add("close grid", GridCommand.Cancel);
        Add("next monitor", GridCommand.NextMonitor);

        var all = new Choices(
            new GrammarBuilder(new SemanticResultKey(GridDigitKey, digits)),
            new GrammarBuilder(new SemanticResultKey(GridCommandKey, commands)));
        return new Grammar(new GrammarBuilder(all)) { Name = "grid" };
    }

    /// <summary>
    /// WASAPI capture from a specific device, resampled to the 16 kHz/16-bit/mono format SAPI expects
    /// via NAudio's own managed resampler (WdlResamplingSampleProvider -- no dependency on Media
    /// Foundation or ACM codecs, so no extra Windows API startup/shutdown to manage). Capture is
    /// push-based (a callback per hardware buffer); SAPI's stream read is pull-based and blocking, so
    /// a paced pump loop bridges the two through <see cref="LiveAudioStream"/>.
    /// </summary>
    private void StartCaptureFromDevice(string microphoneDeviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(microphoneDeviceId);
        _capture = new WasapiCapture(device);
        _ = _logger.InfoAsync($"[voice-diag] capture device format: {_capture.WaveFormat}.");

        var buffered = new BufferedWaveProvider(_capture.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(2) };

        var captureCallbacks = 0L;
        var captureBytes = 0L;
        _capture.DataAvailable += (_, e) =>
        {
            buffered.AddSamples(e.Buffer, 0, e.BytesRecorded);
            captureCallbacks++;
            captureBytes += e.BytesRecorded;
            if (captureCallbacks == 1) _ = _logger.InfoAsync($"[voice-diag] first capture callback: {e.BytesRecorded} bytes.");
            else if (captureCallbacks % 200 == 0) _ = _logger.InfoAsync($"[voice-diag] capture callbacks={captureCallbacks}, total bytes={captureBytes}.");
        };
        _capture.RecordingStopped += (_, e) =>
            _ = _logger.InfoAsync(e.Exception is null ? "[voice-diag] capture stopped (no error)." : $"[voice-diag] capture stopped with error: {e.Exception}.");

        ISampleProvider samples = buffered.ToSampleProvider();
        if (samples.WaveFormat.Channels != 1) samples = new DownmixToMonoSampleProvider(samples);
        var resampled = new WdlResamplingSampleProvider(samples, TargetSampleRate);
        IWaveProvider pcm16 = new SampleToWaveProvider16(resampled);

        _liveStream = new LiveAudioStream(_logger);
        _pumpCts = new CancellationTokenSource();
        var token = _pumpCts.Token;
        _pumpTask = Task.Run(() => PumpAudio(pcm16, _liveStream, token), CancellationToken.None);

        try
        {
            _capture.StartRecording();
            _ = _logger.InfoAsync("[voice-diag] StartRecording() returned without throwing.");
        }
        catch (Exception ex)
        {
            _ = _logger.InfoAsync($"[voice-diag] StartRecording() threw: {ex}.");
            throw;
        }
    }

    /// <summary>Reads resampled audio and forwards it to the live stream, paced to roughly real time so the (non-blocking) resampler pull doesn't spin the CPU faster than audio actually arrives.</summary>
    private void PumpAudio(IWaveProvider source, LiveAudioStream sink, CancellationToken token)
    {
        const int ChunkBytes = 3200; // 100ms at 16kHz/16-bit/mono
        var chunkDurationMs = (int)(ChunkBytes * 1000L / source.WaveFormat.AverageBytesPerSecond);
        var buffer = new byte[ChunkBytes];
        var iterations = 0L;
        var bytesWritten = 0L;

        _ = _logger.InfoAsync($"[voice-diag] pump loop starting, {chunkDurationMs}ms/chunk.");
        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = source.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    sink.Write(buffer, 0, read);
                    bytesWritten += read;
                }

                iterations++;
                if (iterations == 1) _ = _logger.InfoAsync($"[voice-diag] first pump read: {read} bytes.");
                else if (iterations % 100 == 0) _ = _logger.InfoAsync($"[voice-diag] pump iterations={iterations}, bytes written={bytesWritten}.");

                Thread.Sleep(chunkDurationMs);
            }

            _ = _logger.InfoAsync("[voice-diag] pump loop exiting: cancellation requested.");
        }
        catch (Exception ex)
        {
            _ = _logger.InfoAsync($"Voice command audio pump stopped: {ex}");
        }
    }

    private void StopCapture()
    {
        if (_capture is not null)
        {
            try
            {
                _capture.StopRecording();
            }
            catch
            {
                // Best-effort -- the device may already be gone (unplugged, etc).
            }

            _capture.Dispose();
            _capture = null;
        }

        _pumpCts?.Cancel();
        try
        {
            _pumpTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Expected: the pump loop's own catch already logged whatever stopped it.
        }

        _pumpCts?.Dispose();
        _pumpCts = null;
        _pumpTask = null;

        _liveStream?.Dispose();
        _liveStream = null;
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        try
        {
            var text = e.Result.Text;
            var confidence = e.Result.Confidence;

            if (confidence < MinimumConfidence)
            {
                _ = _logger.InfoAsync($"[voice] heard \"{text}\" but confidence {confidence:0.00} is below the {MinimumConfidence:0.00} threshold.");
                return;
            }

            if (e.Result.Semantics.ContainsKey(OpenGridKey))
            {
                _ = _logger.InfoAsync($"[voice] heard \"{text}\" (confidence {confidence:0.00}) -> open grid.");
                GridRequested?.Invoke();
                return;
            }

            if (e.Result.Semantics.ContainsKey(GridDigitKey) && e.Result.Semantics[GridDigitKey].Value is int digit)
            {
                _ = _logger.InfoAsync($"[voice] heard \"{text}\" (confidence {confidence:0.00}) -> grid {digit}.");
                GridDigitSpoken?.Invoke(digit);
                return;
            }

            if (e.Result.Semantics.ContainsKey(GridCommandKey) && e.Result.Semantics[GridCommandKey].Value is int gridCommand)
            {
                _ = _logger.InfoAsync($"[voice] heard \"{text}\" (confidence {confidence:0.00}) -> grid {(GridCommand)gridCommand}.");
                GridCommandSpoken?.Invoke((GridCommand)gridCommand);
                return;
            }

            if (e.Result.Semantics.ContainsKey(MonitorIndexKey) && e.Result.Semantics[MonitorIndexKey].Value is int index)
            {
                _ = _logger.InfoAsync($"[voice] heard \"{text}\" (confidence {confidence:0.00}) -> monitor {index}.");
                MonitorRequested?.Invoke(index);
            }
            else
            {
                _ = _logger.InfoAsync($"[voice] heard \"{text}\" (confidence {confidence:0.00}) but no monitor index in the result.");
            }
        }
        catch (Exception ex)
        {
            // The engine raises this event on its own background thread -- an unhandled exception
            // here would otherwise crash that thread silently instead of just dropping one command.
            _ = _logger.InfoAsync($"Voice command recognition error: {ex.Message}");
        }
    }

    /// <summary>
    /// SAPI applies its own internal confidence rejection before SpeechRecognized ever fires -- an
    /// utterance that's too unclear is reported here instead, silently, with no other visible effect.
    /// Logging it is the only way to see what the recognizer actually heard for tuning.
    /// </summary>
    private void OnSpeechRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
    {
        var best = e.Result?.Alternates.Count > 0 ? e.Result.Alternates[0] : null;
        _ = _logger.InfoAsync(best is null
            ? "[voice] heard something but it was rejected with no usable alternate."
            : $"[voice] rejected \"{best.Text}\" (confidence {best.Confidence:0.00}).");
    }

    public void Dispose()
    {
        if (_engine is null) return;
        _engine.RecognizeAsyncStop();
        _engine.SpeechRecognized -= OnSpeechRecognized;
        _engine.SpeechRecognitionRejected -= OnSpeechRejected;
        _engine.Dispose();
        _engine = null;
        StopCapture();
    }

    /// <summary>Averages an arbitrary channel count down to mono -- simpler and more robust than assuming stereo.</summary>
    private sealed class DownmixToMonoSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var sourceBuffer = new float[count * _channels];
            var samplesRead = source.Read(sourceBuffer, 0, count * _channels);
            var framesRead = samplesRead / _channels;
            for (var i = 0; i < framesRead; i++)
            {
                float sum = 0;
                for (var c = 0; c < _channels; c++) sum += sourceBuffer[(i * _channels) + c];
                buffer[offset + i] = sum / _channels;
            }

            return framesRead;
        }
    }
}
