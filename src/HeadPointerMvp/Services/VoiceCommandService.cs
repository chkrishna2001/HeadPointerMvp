using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Vosk;
using HeadPointerMvp.Infrastructure;

namespace HeadPointerMvp.Services;

/// <summary>
/// Offline voice commands on Vosk (Kaldi). Replaces the old Windows desktop engine (SAPI), which rejected
/// roughly half of clearly spoken commands. Vosk is run with a closed grammar, so it can only ever
/// return our own phrases or "[unk]" - ordinary speech and background chatter come back as "[unk]" and are
/// ignored. Two grammars exist: everyday (grid / monitor N) and grid mode (digits and click commands,
/// speakable in one breath: "five five five click"). Only the active one is fed audio.
/// </summary>
public sealed class VoiceCommandService : IDisposable
{
    private const int TargetSampleRate = 16000;
    private const string ModelFolderName = "vosk-model-small-en-us-0.15";
    private const string Unknown = "[unk]";
    private static readonly string[] NumberWords = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];

    private readonly FileLogger _logger;
    private WasapiCapture? _capture;
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private Model? _model;
    private volatile bool _gridMode;

    public VoiceCommandService(FileLogger logger) => _logger = logger;

    public static string ModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeadPointerMvp", "models", ModelFolderName);

    public event Action<int>? MonitorRequested;

    /// <summary>"grid" / "mouse grid" / "show grid".</summary>
    public event Action? GridRequested;

    /// <summary>A digit 1..9 spoken while the grid is open.</summary>
    public event Action<int>? GridDigitSpoken;

    public event Action<GridCommand>? GridCommandSpoken;

    /// <summary>Switches which grammar is listening: digits/click while the grid is open, grid/monitor otherwise.</summary>
    public void SetGridMode(bool active) => _gridMode = active;

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

    /// <param name="microphoneDeviceId">Empty/null follows Windows' default recording device. Otherwise a WASAPI device ID from <see cref="ListMicrophones"/>.</param>
    public void Start(int monitorCount, string? microphoneDeviceId = null)
    {
        if (monitorCount <= 0 || _pumpTask is not null) return;

        if (!Directory.Exists(ModelPath))
        {
            _ = _logger.InfoAsync($"Voice commands unavailable: speech model not found at {ModelPath}.");
            return;
        }

        try
        {
            Vosk.Vosk.SetLogLevel(-1);
            _model = new Model(ModelPath);
            var count = Math.Min(monitorCount, NumberWords.Length);
            var normalGrammar = BuildGrammar(["grid", "mouse grid", "show grid", "pointer grid", .. MonitorPhrases(count)]);
            var gridGrammar = BuildGrammar([.. NumberWords.Take(9), "click", "select", "right click", "double click", "back", "undo", "cancel", "close grid", "next monitor"]);

            var pcm = StartCapture(microphoneDeviceId);
            _pumpCts = new CancellationTokenSource();
            var token = _pumpCts.Token;
            _pumpTask = Task.Run(() => RecognitionLoop(pcm, normalGrammar, gridGrammar, token), CancellationToken.None);
            _ = _logger.InfoAsync($"Voice commands listening (Vosk offline): \"grid\", \"monitor one..{NumberWords[count - 1]}\"; once the grid is open: digits, click, back, cancel.");
        }
        catch (Exception ex)
        {
            _ = _logger.InfoAsync($"Voice commands unavailable: {ex}");
            StopAll();
        }
    }

    private static IEnumerable<string> MonitorPhrases(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return $"monitor {NumberWords[i]}";
            yield return $"m {NumberWords[i]}";
        }
    }

    private static string BuildGrammar(IEnumerable<string> phrases) => JsonSerializer.Serialize(phrases.Append(Unknown));

    /// <summary>WASAPI capture from the chosen (or default) device, downmixed to mono and resampled to 16 kHz / 16-bit PCM.</summary>
    private IWaveProvider StartCapture(string? microphoneDeviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrEmpty(microphoneDeviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
            : enumerator.GetDevice(microphoneDeviceId);
        _capture = new WasapiCapture(device);

        var buffered = new BufferedWaveProvider(_capture.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(2) };
        _capture.DataAvailable += (_, e) => buffered.AddSamples(e.Buffer, 0, e.BytesRecorded);
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) _ = _logger.InfoAsync($"Voice capture stopped with error: {e.Exception.Message}");
        };

        ISampleProvider samples = buffered.ToSampleProvider();
        if (samples.WaveFormat.Channels != 1) samples = new DownmixToMonoSampleProvider(samples);
        var resampled = new WdlResamplingSampleProvider(samples, TargetSampleRate);
        _capture.StartRecording();
        _ = _logger.InfoAsync($"Voice capture started on \"{device.FriendlyName}\" ({_capture.WaveFormat}).");
        return new SampleToWaveProvider16(resampled);
    }

    private void RecognitionLoop(IWaveProvider source, string normalGrammar, string gridGrammar, CancellationToken token)
    {
        const int ChunkBytes = 3200; // 100 ms at 16 kHz / 16-bit / mono
        var buffer = new byte[ChunkBytes];
        using var normal = new VoskRecognizer(_model, TargetSampleRate, normalGrammar);
        using var grid = new VoskRecognizer(_model, TargetSampleRate, gridGrammar);
        var wasGrid = false;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_gridMode != wasGrid)
                {
                    wasGrid = _gridMode;
                    (wasGrid ? grid : normal).Reset(); // drop any half-heard audio from before the switch
                }

                var read = source.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    var active = wasGrid ? grid : normal;
                    if (active.AcceptWaveform(buffer, read)) HandleResult(active.Result(), wasGrid);
                }

                Thread.Sleep(50);
            }
        }
        catch (Exception ex)
        {
            _ = _logger.InfoAsync($"Voice recognition loop stopped: {ex}");
        }
    }

    private void HandleResult(string json, bool gridMode)
    {
        try
        {
            var text = JsonDocument.Parse(json).RootElement.GetProperty("text").GetString() ?? string.Empty;
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w != Unknown).ToArray();
            if (words.Length == 0) return;

            _ = _logger.InfoAsync($"[voice] heard \"{text}\" ({(gridMode ? "grid" : "normal")} mode).");
            if (gridMode) DispatchGrid(words);
            else DispatchNormal(words);
        }
        catch (Exception ex)
        {
            _ = _logger.InfoAsync($"Voice command error: {ex.Message}");
        }
    }

    private void DispatchNormal(string[] words)
    {
        for (var i = 0; i < words.Length; i++)
        {
            if (words[i] == "grid")
            {
                GridRequested?.Invoke();
            }
            else if (words[i] is "monitor" or "m" && i + 1 < words.Length)
            {
                var index = Array.IndexOf(NumberWords, words[i + 1]);
                if (index < 0) continue;
                MonitorRequested?.Invoke(index);
                i++;
            }
        }
    }

    /// <summary>One utterance can hold several commands ("five five five click"); they are applied in order.</summary>
    private void DispatchGrid(string[] words)
    {
        for (var i = 0; i < words.Length; i++)
        {
            var next = i + 1 < words.Length ? words[i + 1] : string.Empty;
            var digit = Array.IndexOf(NumberWords, words[i]);
            if (digit is >= 0 and < 9)
            {
                GridDigitSpoken?.Invoke(digit + 1);
                continue;
            }

            switch (words[i])
            {
                case "click" or "select":
                    GridCommandSpoken?.Invoke(GridCommand.Click);
                    break;
                case "right" when next == "click":
                    GridCommandSpoken?.Invoke(GridCommand.RightClick);
                    i++;
                    break;
                case "double" when next == "click":
                    GridCommandSpoken?.Invoke(GridCommand.DoubleClick);
                    i++;
                    break;
                case "back" or "undo":
                    GridCommandSpoken?.Invoke(GridCommand.Back);
                    break;
                case "cancel":
                    GridCommandSpoken?.Invoke(GridCommand.Cancel);
                    break;
                case "close" when next == "grid":
                    GridCommandSpoken?.Invoke(GridCommand.Cancel);
                    i++;
                    break;
                case "next" when next == "monitor":
                    GridCommandSpoken?.Invoke(GridCommand.NextMonitor);
                    i++;
                    break;
            }
        }
    }

    private void StopAll()
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
            _pumpTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The loop already logged whatever stopped it.
        }

        _pumpCts?.Dispose();
        _pumpCts = null;
        _pumpTask = null;
        _model?.Dispose();
        _model = null;
    }

    public void Dispose() => StopAll();

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
