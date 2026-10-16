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
public enum VoiceIntentKind
{
    Grid,
    Monitor,
    Press,
    Click,
    RightClick,
    DoubleClick,
    ScrollUp,
    ScrollDown
}

public readonly record struct VoiceIntent(VoiceIntentKind Kind, int Index = 0, string Chord = "");

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
    private string[] _wakeWords = [];
    private readonly Dictionary<string, VoiceIntent> _commands = new(StringComparer.Ordinal);
    private int _longestCommand = 1;

    public VoiceCommandService(FileLogger logger) => _logger = logger;

    public static string ModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeadPointerMvp", "models", ModelFolderName);

    /// <summary>A wake-word command was spoken ("rama press enter"). Raised on the recognizer thread.</summary>
    public event Action<VoiceIntent>? IntentRaised;

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
    public void Start(int monitorCount, string? microphoneDeviceId, string wakeWord, IReadOnlyDictionary<string, string> macros)
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

            _wakeWords = wakeWord.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var missing = _wakeWords.Where(w => _model.FindWord(w) < 0).ToArray();
            if (_wakeWords.Length == 0 || missing.Length > 0)
            {
                _ = _logger.InfoAsync($"Voice commands unavailable: wake word \"{wakeWord}\" is not in the speech model's vocabulary ({string.Join(", ", missing)}). Pick another word in settings.json (VoiceWakeWord).");
                StopAll();
                return;
            }

            BuildCommands(count, macros);
            var normalGrammar = BuildGrammar(_commands.Keys.Select(phrase => $"{string.Join(' ', _wakeWords)} {phrase}"));
            var gridGrammar = BuildGrammar([.. NumberWords.Take(9), "click", "select", "right click", "double click", "back", "undo", "cancel", "close grid", "next monitor"]);

            var pcm = StartCapture(microphoneDeviceId);
            _pumpCts = new CancellationTokenSource();
            var token = _pumpCts.Token;
            _pumpTask = Task.Run(() => RecognitionLoop(pcm, normalGrammar, gridGrammar, token), CancellationToken.None);
            _ = _logger.InfoAsync($"Voice commands listening (Vosk offline). Say \"{wakeWord}\" + a command (show grid, monitor N, press <key>, click, scroll, macros); once the grid is open, digits/click/back/cancel need no wake word.");
        }
        catch (Exception ex)
        {
            _ = _logger.InfoAsync($"Voice commands unavailable: {ex}");
            StopAll();
        }
    }

    private static readonly (string Spoken, string Key)[] SpokenKeys =
    [
        ("enter", "Enter"), ("tab", "Tab"), ("escape", "Escape"), ("space", "Space"), ("back space", "Back"), ("delete", "Delete"),
        ("up", "Up"), ("down", "Down"), ("left", "Left"), ("right", "Right"), ("home", "Home"), ("end", "End"),
        ("page up", "PageUp"), ("page down", "PageDown")
    ];

    private static readonly (string Spoken, string Chord)[] SpokenModifiers =
        [("", ""), ("control ", "Ctrl+"), ("alt ", "Alt+"), ("shift ", "Shift+"), ("control shift ", "Ctrl+Shift+"), ("control alt ", "Ctrl+Alt+")];

    /// <summary>The command table: everything that can follow the wake word. Phrases using words the model doesn't know are dropped (and logged).</summary>
    private void BuildCommands(int monitorCount, IReadOnlyDictionary<string, string> macros)
    {
        _commands.Clear();
        var dropped = new List<string>();

        void Add(string phrase, VoiceIntent intent)
        {
            phrase = phrase.ToLowerInvariant().Trim();
            if (phrase.Split(' ').Any(word => _model!.FindWord(word) < 0)) dropped.Add(phrase);
            else _commands[phrase] = intent;
        }

        foreach (var phrase in new[] { "grid", "show grid", "mouse grid", "pointer grid" }) Add(phrase, new VoiceIntent(VoiceIntentKind.Grid));
        for (var i = 0; i < monitorCount; i++)
        {
            Add($"monitor {NumberWords[i]}", new VoiceIntent(VoiceIntentKind.Monitor, i));
            Add($"m {NumberWords[i]}", new VoiceIntent(VoiceIntentKind.Monitor, i));
        }

        foreach (var (spokenModifier, chordModifier) in SpokenModifiers)
        {
            foreach (var (spokenKey, key) in SpokenKeys) Add($"press {spokenModifier}{spokenKey}", new VoiceIntent(VoiceIntentKind.Press, 0, chordModifier + key));
        }

        Add("click", new VoiceIntent(VoiceIntentKind.Click));
        Add("right click", new VoiceIntent(VoiceIntentKind.RightClick));
        Add("double click", new VoiceIntent(VoiceIntentKind.DoubleClick));
        Add("scroll up", new VoiceIntent(VoiceIntentKind.ScrollUp));
        Add("scroll down", new VoiceIntent(VoiceIntentKind.ScrollDown));
        foreach (var (phrase, chord) in macros) Add(phrase, new VoiceIntent(VoiceIntentKind.Press, 0, chord));

        _longestCommand = _commands.Keys.Max(phrase => phrase.Count(c => c == ' ') + 1);
        if (dropped.Count > 0) _ = _logger.InfoAsync($"Voice phrases dropped (word not in the speech model): {string.Join("; ", dropped)}.");
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

    /// <summary>
    /// Only words after the wake word count; anything before it is ignored, so "I'll press enter later"
    /// does nothing but "rama press enter" does. Several commands may follow one wake word, and a second
    /// wake word starts over.
    /// </summary>
    private void DispatchNormal(string[] words)
    {
        var i = 0;
        var awake = false;
        while (i < words.Length)
        {
            if (MatchesAt(words, i, _wakeWords))
            {
                awake = true;
                i += _wakeWords.Length;
                continue;
            }

            if (!awake)
            {
                i++;
                continue;
            }

            var matched = false;
            for (var length = Math.Min(_longestCommand, words.Length - i); length >= 1 && !matched; length--)
            {
                if (!_commands.TryGetValue(string.Join(' ', words.Skip(i).Take(length)), out var intent)) continue;
                IntentRaised?.Invoke(intent);
                i += length;
                matched = true;
            }

            if (!matched) i++;
        }
    }

    private static bool MatchesAt(string[] words, int index, string[] phrase)
    {
        if (index + phrase.Length > words.Length) return false;
        for (var k = 0; k < phrase.Length; k++)
        {
            if (words[index + k] != phrase[k]) return false;
        }

        return true;
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
