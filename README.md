# HeadPointer MVP

HeadPointer is a tray-first Windows utility for multi-monitor work. It supports two independent, toggleable input methods for choosing a monitor: webcam head-pose (wait for a deliberate left/right/center head turn) and offline voice commands ("monitor one", "monitor two", ...). Either way, it places the pointer at the target monitor's working-area centre, fires a tray notification naming the new active monitor (toggleable), and by default brings the topmost window there to the foreground so typing follows the pointer (this includes RDP client windows, since it's a normal local window-focus change, not input injected into the remote session); turn off `Activate window under pointer after a move` in Settings to go back to cursor-only moves.

## Current MVP boundary

The application is a Windows tray app. It is unpackaged deliberately, so it can be installed per user and registered under the current-user startup key without elevation.

The pose source is an external-process boundary: camera capture and face detection run in their own process (`HeadPointerMvp.CameraBridge`), so a crash or CPU spike there never touches the tray app. The included `OpenSeeFaceHeadPoseSource` receives local UDP JSON packets such as:

```json
{ "yaw": -18.4, "pitch": 2.1, "roll": 0.3 }
```

The bridge should only listen on `127.0.0.1`. Negative yaw means look left and positive yaw means look right. `src/HeadPointerMvp.CameraBridge` is the native C#/OpenCvSharp webcam bridge (no Python required). `tools/openseeface-bridge` documents the same adapter contract for OpenSeeFace if you'd rather use that instead.

## Architecture

```text
webcam → HeadPointerMvp.CameraBridge (OpenCvSharp + YuNet) → local UDP pose packet → IHeadPoseSource
      → HeadDirectionRouter (EMA smoothing + dwell + cooldown) → PointerService → Win32 cursor
mic → VoiceCommandService (System.Speech, fixed grammar) ───────────────────────┘
tray icon / hotkeys / settings ────────────────────────────────────────────────┘
```

`HeadPointerMvp.CameraBridge` is a small standalone console app: it opens the webcam via OpenCV, runs OpenCV's bundled YuNet face detector (a ~230 KB ONNX model, ships in `Assets/`) at a capped ~15 fps on a downscaled 320×240 frame to keep CPU/GPU usage low, derives yaw from the eye/nose landmarks YuNet returns, and sends it over loopback UDP in the same packet format the tray app already expects. It has no Python dependency — it's a .NET project referenced by the tray app's `.csproj` (build-order only) and its output is copied into the tray app's own output folder under `CameraBridge\` at build time.

- `Domain`: persisted user choices and head-pose value type.
- `Services`: pose ingestion, monitor ordering, safe routing, pointer movement, hotkeys, startup and tray integration.
- `Infrastructure`: app-local settings path and append-only local logging.

## Use

1. Install the .NET 8 SDK (no Python needed — the camera bridge is native C#/OpenCvSharp).
2. Restore and run `HeadPointerMvp.sln` from Visual Studio 2022 or `dotnet run --project src/HeadPointerMvp`. Building the solution also builds `HeadPointerMvp.CameraBridge` and copies it (plus the YuNet model) into the tray app's output folder.
3. Camera tracking starts automatically with the app — no manual step. The tray menu is deliberately minimal: **Settings…**, **Enable / Disable**, **Exit**.
4. Open **Settings** and calibrate: for each monitor, look at it and click **Capture** (the live yaw readout updates as you turn your head; pointer movement is automatically paused while Settings is open so the cursor doesn't jump around mid-calibration). Calibrate at least two monitors. Capture/Clear save immediately — no need to also click the main Save button for calibration to stick.
5. Start conservatively: threshold `12°`, dwell `450 ms`, cooldown `900 ms`. Tune one setting at a time.

### Input methods

Two independent toggles in Settings control which input methods are live — neither is exclusive, both can run at once:

- **Camera tracking** (`CameraTrackingEnabled`, default on) — starts/stops `HeadPointerMvp.CameraBridge.exe` itself, not just whether it can move the pointer.
- **Voice commands** (`VoiceCommandsEnabled`, default off) — starts/stops `VoiceCommandService`, which uses Windows' built-in offline speech recognition (no cloud, no network calls, no audio ever stored). It listens for a small fixed grammar only — say "m one" or "monitor one" for your first monitor, "m two" / "monitor two" for your second, and so on (1-indexed to match how a person actually counts monitors, and matching the "Monitor 1" / "Monitor 2" labels in the calibration section) — rather than free dictation, both to keep CPU usage minimal and so a stray number in normal conversation doesn't trigger it. Saying a number past your actual monitor count (e.g. "four" on a 3-monitor setup) is correctly out of grammar and just doesn't match, rather than silently landing on the wrong monitor. Recognized commands below 40% confidence are logged and ignored (check `%LocalAppData%\HeadPointerMvp\logs\` for `[voice]` lines — every heard/rejected attempt is logged with its confidence, useful for tuning). It's off by default since it means an always-listening microphone; turn it on in Settings if you want it. If the monitor count changes (a display is connected/disconnected) you'll need to reopen Settings to rebuild the grammar (toggle Voice commands off and on, or restart the app).

  SAPI5 (the underlying 20-year-old desktop recognition engine) has no acoustic training out of the box, so accuracy and required volume can be poor until you train it: Windows' **Speech Recognition** (Control Panel → Ease of Access → Speech Recognition → "Train your computer to better understand you") builds a real acoustic profile for your voice/mic and is the single biggest lever for both "I need to be very loud" and number misrecognition — worth doing once if voice commands feel unreliable.

  **Microphone** (Settings, default "System default") picks which capture device voice commands actually listen on. `System.Speech.Recognition` has no built-in per-device API — it only offers "whatever Windows' system default recording device is" — so picking a specific microphone here captures audio directly from that device via WASAPI (`NAudio.CoreAudioApi.WasapiCapture`), resamples it in-process to the 16 kHz/16-bit/mono PCM format SAPI expects (`NAudio`'s own managed resampler, no Media Foundation/ACM dependency), and feeds that stream into the recognizer (`SpeechRecognitionEngine.SetInputToAudioStream`) instead of touching any Windows-wide setting — other apps' audio/calls are completely unaffected. Changing the microphone selection requires toggling Voice commands off and back on (same as a monitor-count change) since the capture pipeline is only (re)built when the engine starts.

Both input methods still respect the master **Enable tracking** toggle/hotkeys — turning that off (`Ctrl+Alt+P`) pauses pointer movement from either source without stopping the underlying subsystems. This is also the most common reason a voice command or head-turn silently "does nothing": check that toggle first, and check the log for `[voice] ... ignored: master 'Enable tracking' is off.` if in doubt.

### Monitor routing: calibration vs. fixed threshold

`HeadDirectionRouter` picks a target monitor two ways:

- **Calibrated (recommended once you've captured 2+ monitors):** on every pose sample it picks whichever calibrated monitor's captured yaw is closest to your current yaw. This is layout-agnostic — it doesn't care whether Windows' display-arrangement order matches your physical desk (e.g. a laptop screen sitting physically *between* two side monitors, but ordered last in Windows' arrangement). Each monitor is identified by its stable `Screen.DeviceName`, so calibration survives resolution changes and app restarts.
- **Fixed threshold (fallback, used when fewer than 2 monitors are calibrated):** the original MVP behavior — yaw past `Yaw threshold` hops one monitor left/right from `Center monitor index`. This only really works when monitors are laid out in a simple left-to-right line matching Windows' arrangement order, and can't reach past the immediate neighbor of the center monitor.

Recalibrate any time by clicking **Capture** again for a monitor (overwrites its stored yaw), or **Clear** to drop a monitor back out of calibrated routing.

`Ctrl+Alt+H` toggles tracking; `Ctrl+Alt+P` pauses it. The app starts enabled but will not move the pointer until a pose packet arrives.

If the tray icon appears but head movement does nothing, the usual cause is that the camera bridge isn't currently sending JSON pose packets to `127.0.0.1:11573` — check the app's log file (`%LocalAppData%\HeadPointerMvp\logs\`) for `[camera bridge]` lines, e.g. `Could not open camera index 0` if another app is holding the webcam.

The bridge process is tied to the tray app's lifetime via a Windows job object (`JobObject.cs`): if the tray app exits, crashes, or is killed from Task Manager, the bridge is force-killed with it, so the camera light never stays on with no app attached.

Only one instance of the tray app can run at a time (a named `Mutex` in `Program.cs`) — a second launch shows "HeadPointer is already running" and exits immediately rather than starting. Without this, two instances would fight over the same UDP pose-receiver port, the same global hotkeys, and could both have their own voice-recognition engine listening on the mic simultaneously — surfacing as things randomly not working, since whichever instance lost each race failed silently (logged, but easy to miss).

### Window activation on move

`PointerService.ActivateTopWindow` tries `SetForegroundWindow` (via `AttachThreadInput`) first, then checks whether it actually took effect. Windows silently refuses that call in some cases — most commonly when the target window belongs to a more-privileged (elevated) process than the tray app. When that happens, and only if **Allow a simulated click as an activation fallback** is checked in Settings (default on), it falls back to simulating a real right-click at the cursor's new position via `SendInput`, since the window manager honors genuine input regardless of privilege level, followed immediately by an `Escape` keypress in case the click opened a context menu, so nothing is left visibly open. Turn that setting off if you'd rather activation silently do nothing than ever risk a simulated click landing somewhere unexpected.

**Show a notification when the active monitor changes** (Settings, default on) controls the tray balloon that names the new monitor after every move — from either camera tracking or voice commands.

### Resource usage

Measured on this dev machine (32 logical cores) with `Cv2.SetNumThreads(2)` capping OpenCV's internal thread pool (uncapped, OpenCV parallelizes across every core by default, which nearly tripled CPU time for no benefit on a model this small):

- **CPU:** roughly 15–20% of one core, sustained, at the ~15 fps capture rate.
- **Memory:** ~135–140 MB working set, ~200 MB private bytes (mostly fixed .NET runtime + OpenCV native library overhead, not workload-dependent).
- **Threads:** ~25 (down from ~57 uncapped).

The tray app itself (WinForms, no polling loops) is negligible on top of that.

To test the pose path without a webcam bridge, run the app, then run:

```powershell
.\tools\pose-sender\send-pose.ps1 -Yaw 20
```

Use `-Yaw -20` to simulate looking left, `-Yaw 0` for center, and `-Yaw 20` for right.

To run the camera bridge manually (e.g. with a preview window, to sanity-check detection or yaw sign):

```powershell
.\tools\camera-bridge\run-bridge.ps1 -ShowPreview
```

If left and right are reversed, stop it and run:

```powershell
.\tools\camera-bridge\run-bridge.ps1 -ShowPreview -InvertYaw
```

(there's no UI toggle for invert yet — set it by passing `-InvertYaw` when running manually, or edit `AppHost.StartCameraTracking` to pass `--invert-yaw`.)

## Implementation plan

1. **Validate the MVP:** use the adapter contract and tune monitor order, yaw sign, threshold/dwell/cooldown for the desk layout.
2. **Camera integration:** camera capture + face detection now run natively in `HeadPointerMvp.CameraBridge` (OpenCvSharp + YuNet). Per-monitor yaw calibration replaces the old fixed-threshold-only routing. Still open: an in-app camera picker/preview.
3. **Ergonomics/safety:** add a temporary hold-to-enable mode, cursor-position memory per screen, a clear recording LED, and no-move zones. Voice commands now cover the "non-camera fallback" need. Still open: per-monitor hotkeys (`PointerService.MoveToMonitor` and `HotKeyService` already support this, just need wiring) as a third input method, or dedicated eye-tracker hardware for higher accuracy than webcam head-pose.
4. **Productionize:** add structured log retention, crash reporting opt-in, an MSIX installer, code signing, and UI tests plus hardware-in-the-loop tests.

## Safety and privacy

Only head-pose numbers are consumed by this process. No camera frames are stored. Keep the adapter bound to loopback and use the tray toggle to disable motion before making configuration changes.

Voice commands (off by default) use Windows' built-in offline speech recognition — no audio leaves the machine, nothing is recorded to disk, and recognition is limited to a fixed "monitor &lt;number&gt;" grammar rather than continuous transcription.
