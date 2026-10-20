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
    GridClose,
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
    // Best first: the dynamic-graph model is far more accurate than the small one and supports our runtime grammar.
    private static readonly string[] ModelFolderNames = ["vosk-model-en-us-0.22-lgraph", "vosk-model-small-en-us-0.15"];
    private const double MinSpeechRms = 300;       // absolute floor (16-bit units) below which nothing counts as speech
    private const double SpeechOverNoise = 2.5;    // chunk must be this many times louder than the tracked noise floor
    private const double MinWordConfidence = 0.3;
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
    private DateTime _awakeUntil = DateTime.MinValue;
    private readonly List<string> _pending = [];
    private int _peak;
    private AudioEndpointVolume? _endpointVolume;
    private static readonly TimeSpan WakeWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StableUtterance = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan FollowUpWindow = TimeSpan.FromSeconds(3);

    public VoiceCommandService(FileLogger logger) => _logger = logger;

    private static string ModelsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeadPointerMvp", "models");

    public static string ModelPath => ModelFolderNames.Select(name => Path.Combine(ModelsRoot, name)).FirstOrDefault(Directory.Exists)
        ?? Path.Combine(ModelsRoot, ModelFolderNames[0]);

    /// <summary>A wake-word command was spoken ("rama press enter"). Raised on the recognizer thread.</summary>
    public event Action<VoiceIntent>? IntentRaised;

    /// <summary>A digit 1..9 spoken while the grid is open.</summary>
    public event Action<int>? GridDigitSpoken;

    /// <summary>Audible/visible acknowledgement: "wake" (heard the wake word), "ok" (ran a command; text = phrase), "miss" (woke but no command followed).</summary>
    public event Action<string, string>? Feedback;
    private bool _awaiting;

    public event Action<GridCommand>? GridCommandSpoken;

    /// <summary>A NATO word naming a first-level grid cell (alpha = 1 .. papa = 16).</summary>
    public event Action<int>? GridWordSpoken;

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
            _ = _logger.InfoAsync($"Voice model: {Path.GetFileName(ModelPath)}.");
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
            var wakePhrase = string.Join(' ', _wakeWords);
            // Whole phrases only decode when spoken in one go, so the grammar also accepts the wake word alone and every
            // command without it: "rama ... (pause) ... click" works. Bare commands are ignored unless the wake word was heard recently.
            var normalGrammar = BuildGrammar([wakePhrase, .. _commands.Keys.Select(phrase => $"{wakePhrase} {phrase}"), .. _commands.Keys]);
            var gridGrammar = BuildGrammar([.. NumberWords.Take(9), .. GridPointerService.CellWords, "click", "tap", "enter", "select", "right click", "double click", "back", "undo", "cancel", "close grid", "next monitor",
                .. _commands.Keys.Select(phrase => $"{wakePhrase} {phrase}")]);

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

    private static readonly string[] PhoneticLetters =
    [
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima", "mike",
        "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey", "x ray", "yankee", "zulu"
    ];

    /// <summary>Built-in window / app / browser commands ("rama close window"); each is just a key chord.</summary>
    private static readonly (string Spoken, string Chord)[] AppCommands =
    [
        ("close window", "Alt+F4"), ("close app", "Alt+F4"),
        ("switch window", "Alt+Tab"), ("switch app", "Alt+Tab"), ("task view", "Win+Tab"),
        ("minimize", "Win+Down"), ("maximize", "Win+Up"),
        ("snap left", "Win+Left"), ("snap right", "Win+Right"),
        ("show desktop", "Win+D"), ("start menu", "Win"), ("search", "Win+S"),
        ("new tab", "Ctrl+T"), ("close tab", "Ctrl+W"), ("reopen tab", "Ctrl+Shift+T"),
        ("next tab", "Ctrl+Tab"), ("previous tab", "Ctrl+Shift+Tab"),
        ("go back", "Alt+Left"), ("go forward", "Alt+Right"), ("refresh", "F5"), ("address bar", "Ctrl+L"),
        ("copy", "Ctrl+C"), ("cut", "Ctrl+X"), ("paste", "Ctrl+V"), ("undo", "Ctrl+Z"), ("redo", "Ctrl+Y"),
        ("select all", "Ctrl+A"), ("save", "Ctrl+S"), ("find", "Ctrl+F"),
        ("top", "Ctrl+Home"), ("bottom", "Ctrl+End")
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
        foreach (var phrase in new[] { "close grid", "hide grid", "cancel grid" }) Add(phrase, new VoiceIntent(VoiceIntentKind.GridClose));
        for (var i = 0; i < monitorCount; i++)
        {
            Add($"monitor {NumberWords[i]}", new VoiceIntent(VoiceIntentKind.Monitor, i));
            Add($"m {NumberWords[i]}", new VoiceIntent(VoiceIntentKind.Monitor, i));
        }

        foreach (var (spokenModifier, chordModifier) in SpokenModifiers)
        {
            foreach (var (spokenKey, key) in SpokenKeys)
            {
                var intent = new VoiceIntent(VoiceIntentKind.Press, 0, chordModifier + key);
                Add($"press {spokenModifier}{spokenKey}", intent);
                Add($"{spokenModifier}{spokenKey}", intent); // "rama alt tab" works too: 'press' is often swallowed by the recognizer
            }
        }

        // Letters only after a modifier ("rama control t"), so a stray letter in conversation never types.
        foreach (var (spokenModifier, chordModifier) in SpokenModifiers.Where(m => m.Chord.Length > 0))
        {
            // Spoken as phonetic words: bare letters ("t" vs "b") were confused by the recognizer.
            for (var i = 0; i < PhoneticLetters.Length; i++)
            {
                var intent = new VoiceIntent(VoiceIntentKind.Press, 0, chordModifier + (char)('A' + i));
                Add($"press {spokenModifier}{PhoneticLetters[i]}", intent);
                Add($"{spokenModifier}{PhoneticLetters[i]}", intent);
            }
        }

        Add("click", new VoiceIntent(VoiceIntentKind.Click));
        Add("right click", new VoiceIntent(VoiceIntentKind.RightClick));
        Add("double click", new VoiceIntent(VoiceIntentKind.DoubleClick));
        Add("scroll up", new VoiceIntent(VoiceIntentKind.ScrollUp));
        Add("scroll down", new VoiceIntent(VoiceIntentKind.ScrollDown));
        foreach (var (phrase, chord) in AppCommands) Add(phrase, new VoiceIntent(VoiceIntentKind.Press, 0, chord));
        foreach (var (phrase, chord) in macros) Add(phrase, new VoiceIntent(VoiceIntentKind.Press, 0, chord)); // user macros override built-ins

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
        _endpointVolume = device.AudioEndpointVolume;
        _ = _logger.InfoAsync($"Voice capture started on \"{device.FriendlyName}\" ({_capture.WaveFormat}), input level {InputLevel()}.");
        return new SampleToWaveProvider16(resampled);
    }

    private string InputLevel()
    {
        try
        {
            return _endpointVolume is null ? "unknown" : $"{_endpointVolume.MasterVolumeLevelScalar * 100:0}%";
        }
        catch
        {
            return "unknown";
        }
    }

    private void RecognitionLoop(IWaveProvider source, string normalGrammar, string gridGrammar, CancellationToken token)
    {
        const int ChunkBytes = 3200; // 100 ms at 16 kHz / 16-bit / mono
        var buffer = new byte[ChunkBytes];
        using var normal = new VoskRecognizer(_model, TargetSampleRate, normalGrammar);
        using var grid = new VoskRecognizer(_model, TargetSampleRate, gridGrammar);
        normal.SetWords(true);
        grid.SetWords(true);
        var silence = new byte[ChunkBytes];
        var previous = new byte[ChunkBytes];
        var previousRead = 0;
        var wasGated = true;
        var noiseFloor = MinSpeechRms / SpeechOverNoise;
        var speechUntil = DateTime.MinValue;
        var wasGrid = false;
        var lastPartial = string.Empty;
        var partialChangedAt = DateTime.UtcNow;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_gridMode != wasGrid)
                {
                    wasGrid = _gridMode;
                    (wasGrid ? grid : normal).Reset(); // drop any half-heard audio from before the switch
                }

                if (_awaiting && DateTime.UtcNow > _awakeUntil)
                {
                    _awaiting = false;
                    _pending.Clear();
                    Feedback?.Invoke("miss", string.Empty);
                }

                var read = source.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    var active = wasGrid ? grid : normal;
                    double energy = 0;
                    for (var b = 0; b + 1 < read; b += 2)
                    {
                        var sample = BitConverter.ToInt16(buffer, b);
                        _peak = Math.Max(_peak, Math.Abs((int)sample));
                        energy += (double)sample * sample;
                    }

                    // Noise gate: track the background level (falls fast, rises slowly) and only pass audio that
                    // stands clearly above it. Gated chunks become digital silence, so steady fan/room/headset
                    // noise can't be mistaken for words and the recognizer can end utterances on its own.
                    var rms = Math.Sqrt(energy / (read / 2));
                    noiseFloor = rms < noiseFloor ? rms : noiseFloor + ((rms - noiseFloor) * 0.02);
                    var loud = rms > Math.Max(MinSpeechRms, noiseFloor * SpeechOverNoise);
                    if (loud) speechUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
                    var gated = DateTime.UtcNow > speechUntil;
                    if (gated && lastPartial.Length == 0) _peak = 0;
                    var chunk = gated ? silence : buffer;
                    // Pre-roll: the chunk before the gate opened holds the start of the word (the "k" of click); feed it back in.
                    if (!gated && wasGated && previousRead > 0) active.AcceptWaveform(previous, previousRead);
                    wasGated = gated;
                    Buffer.BlockCopy(buffer, 0, previous, 0, read);
                    previousRead = read;
                    if (active.AcceptWaveform(chunk, read))
                    {
                        HandleResult(active.Result(), wasGrid);
                        _peak = 0;
                        lastPartial = string.Empty;
                    }
                    else
                    {
                        // The recognizer only closes an utterance after a stretch of true silence; with a noisy
                        // headset or room that can take far too long (or never come). If the in-progress text has
                        // stopped changing, close the utterance ourselves.
                        var partial = JsonDocument.Parse(active.PartialResult()).RootElement.GetProperty("partial").GetString() ?? string.Empty;
                        if (partial != lastPartial)
                        {
                            lastPartial = partial;
                            partialChangedAt = DateTime.UtcNow;
                        }
                        else if (partial.Length > 0 && DateTime.UtcNow - partialChangedAt > StableUtterance)
                        {
                            HandleResult(active.FinalResult(), wasGrid);
                            _peak = 0;
                            lastPartial = string.Empty;
                        }
                    }
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
            var low = new List<string>();
            if (JsonDocument.Parse(json).RootElement.TryGetProperty("result", out var detail))
            {
                var confident = new List<string>();
                foreach (var item in detail.EnumerateArray())
                {
                    var word = item.GetProperty("word").GetString() ?? string.Empty;
                    if (word == Unknown) continue;
                    if (item.GetProperty("conf").GetDouble() >= MinWordConfidence || _wakeWords.Contains(word)) confident.Add(word);
                    else low.Add(word);
                }

                words = [.. confident];
                text = string.Join(' ', confident);
                if (low.Count > 0) _ = _logger.InfoAsync($"[voice] dropped low-confidence words: {string.Join(' ', low)}.");
            }
            if (words.Length == 0)
            {
                // Speech was heard but matched nothing in the grammar; the peak (0-32767) shows whether the mic is just too quiet.
                if (text.Length > 0) _ = _logger.InfoAsync($"[voice] heard speech but nothing usable (peak {_peak}).");
                return;
            }

            _ = _logger.InfoAsync($"[voice] heard \"{text}\" ({(gridMode ? "grid" : "normal")} mode, peak {_peak}, input level {InputLevel()}).");
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
        // The recognizer ends a result at every short pause, so "rama ... press ... tab" often arrives as
        // separate results. Hearing the wake word (or running a command) therefore leaves a brief window in
        // which the next result still counts, and a half-heard command ("press") is carried into it.
        var awake = DateTime.UtcNow < _awakeUntil;
        if (awake && _pending.Count > 0) words = [.. _pending, .. words];
        _pending.Clear();
        var consumedTo = 0;
        while (i < words.Length)
        {
            if (MatchesAt(words, i, _wakeWords))
            {
                awake = true;
                _awaiting = true;
                Feedback?.Invoke("wake", string.Empty);
                _awakeUntil = DateTime.UtcNow + WakeWindow;
                i += _wakeWords.Length;
                consumedTo = i;
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
                _awaiting = false;
                Feedback?.Invoke("ok", string.Join(' ', words.Skip(i).Take(length)));
                _awakeUntil = DateTime.UtcNow + FollowUpWindow;
                i += length;
                matched = true;
            }

            if (matched) consumedTo = i;
            else i++;
        }

        if (!awake) return;
        // Words after the last wake word / command are probably the start of one ("press"): keep them for the next result.
        _pending.AddRange(words.Skip(consumedTo).Where(w => !MatchesAt([w], 0, _wakeWords)).TakeLast(3));
        if (_pending.Count > 0) _awakeUntil = DateTime.UtcNow + WakeWindow;
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

    /// <summary>In grid mode, anything from the wake word onward is an ordinary command ("rama monitor two"); the part before it is grid words.</summary>
    private void DispatchGrid(string[] words)
    {
        for (var i = 0; i < words.Length; i++)
        {
            if (!MatchesAt(words, i, _wakeWords)) continue;
            DispatchGridWords(words[..i]);
            DispatchNormal(words[i..]);
            return;
        }

        DispatchGridWords(words);
    }

    /// <summary>One utterance can hold several commands ("five five five click"); they are applied in order.</summary>
    private void DispatchGridWords(string[] words)
    {
        for (var i = 0; i < words.Length; i++)
        {
            var next = i + 1 < words.Length ? words[i + 1] : string.Empty;
            var cellWord = Array.IndexOf(GridPointerService.CellWords, words[i]);
            if (cellWord >= 0)
            {
                GridWordSpoken?.Invoke(cellWord + 1);
                continue;
            }

            var digit = Array.IndexOf(NumberWords, words[i]);
            if (digit is >= 0 and < 9)
            {
                GridDigitSpoken?.Invoke(digit + 1);
                continue;
            }

            switch (words[i])
            {
                case "click" or "tap" or "enter" or "select":
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
