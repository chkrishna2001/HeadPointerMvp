using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OpenCvSharp;

namespace HeadPointerMvp.CameraBridge;

/// <summary>
/// Native replacement for the old Python/mediapipe bridge. Runs as its own process (same
/// external-process boundary the tray app already expects) but needs no Python install: camera
/// capture and face landmark detection both happen via OpenCvSharp's bundled YuNet ONNX model.
/// </summary>
internal static class Program
{
    private const int DetectWidth = 320;
    private const int DetectHeight = 240;
    private const int TargetFrameIntervalMs = 66; // ~15 fps caps CPU/GPU use; head pose doesn't need more.
    private const float ScoreThreshold = 0.75f;

    // YuNet's output row: x, y, w, h, x_re, y_re, x_le, y_le, x_nt, y_nt, x_rcm, y_rcm, x_lcm, y_lcm, score
    private const int RightEyeXCol = 4;
    private const int LeftEyeXCol = 6;
    private const int NoseXCol = 8;
    private const int ScoreCol = 14;

    private static int Main(string[] args)
    {
        BridgeOptions options;
        try
        {
            options = BridgeOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        // OpenCV's DNN backend auto-parallelizes across every logical core by default, which is pure
        // thread-spinup overhead for a model this small (~230 KB) running at ~15 fps. Cap it.
        Cv2.SetNumThreads(2);

        var modelPath = Path.Combine(AppContext.BaseDirectory, "Assets", "face_detection_yunet_2023mar.onnx");
        if (!File.Exists(modelPath))
        {
            Console.Error.WriteLine($"Missing face detector model at {modelPath}.");
            return 2;
        }

        using var capture = new VideoCapture(options.Camera);
        if (!capture.IsOpened())
        {
            Console.Error.WriteLine($"Could not open camera index {options.Camera}.");
            return 3;
        }

        capture.Set(VideoCaptureProperties.FrameWidth, 640);
        capture.Set(VideoCaptureProperties.FrameHeight, 480);

        using var detector = FaceDetectorYN.Create(modelPath, string.Empty, new Size(DetectWidth, DetectHeight), ScoreThreshold);
        using var udp = new UdpClient();
        var target = new IPEndPoint(IPAddress.Parse(options.Host), options.Port);

        Console.WriteLine($"HeadPointer native camera bridge sending to {options.Host}:{options.Port}.");
        Console.WriteLine("Press Ctrl+C to stop.");

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        RunLoop(capture, detector, udp, target, options, stop.Token);

        if (options.ShowPreview) Cv2.DestroyAllWindows();
        return 0;
    }

    private static void RunLoop(VideoCapture capture, FaceDetectorYN detector, UdpClient udp, IPEndPoint target, BridgeOptions options, CancellationToken cancellationToken)
    {
        using var frame = new Mat();
        using var resized = new Mat();
        using var faces = new Mat();
        var lastPrint = DateTime.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            var loopStart = Environment.TickCount64;

            if (!capture.Read(frame) || frame.Empty())
            {
                cancellationToken.WaitHandle.WaitOne(100);
                continue;
            }

            Cv2.Resize(frame, resized, new Size(DetectWidth, DetectHeight));
            detector.Detect(resized, faces);

            if (faces.Rows > 0 && faces.At<float>(0, ScoreCol) >= ScoreThreshold)
            {
                var rightEyeX = faces.At<float>(0, RightEyeXCol);
                var rightEyeY = faces.At<float>(0, RightEyeXCol + 1);
                var leftEyeX = faces.At<float>(0, LeftEyeXCol);
                var leftEyeY = faces.At<float>(0, LeftEyeXCol + 1);
                var noseX = faces.At<float>(0, NoseXCol);
                var noseY = faces.At<float>(0, NoseXCol + 1);

                // Measure the nose offset along and across the eye line instead of the raw image x/y, so
                // tilting the head (roll) can't masquerade as a turn (yaw) or a nod (pitch).
                var axisX = (double)(leftEyeX - rightEyeX);
                var axisY = (double)(leftEyeY - rightEyeY);
                if (axisX < 0) { axisX = -axisX; axisY = -axisY; }
                var eyeDistance = Math.Max(Math.Sqrt((axisX * axisX) + (axisY * axisY)), 0.001);
                var ux = axisX / eyeDistance;
                var uy = axisY / eyeDistance;
                var offsetX = noseX - ((leftEyeX + rightEyeX) / 2.0);
                var offsetY = noseY - ((leftEyeY + rightEyeY) / 2.0);

                var yaw = ((offsetX * ux) + (offsetY * uy)) / eyeDistance * 45.0;
                // Nose drop below the eye line, in the same arbitrary units. Looking down at the keyboard
                // shifts this a lot; the tray app uses its deviation from a running baseline to freeze routing.
                var pitch = ((offsetY * ux) - (offsetX * uy)) / eyeDistance * 45.0;
                var roll = Math.Atan2(axisY, axisX) * 180.0 / Math.PI;
                if (options.InvertYaw) yaw = -yaw;

                var packet = JsonSerializer.SerializeToUtf8Bytes(new { yaw, pitch, roll });
                udp.Send(packet, packet.Length, target);

                if ((DateTime.UtcNow - lastPrint).TotalSeconds > 1)
                {
                    Console.WriteLine($"yaw={yaw,6:0.0} pitch={pitch,6:0.0}");
                    lastPrint = DateTime.UtcNow;
                }
            }

            if (options.ShowPreview)
            {
                Cv2.ImShow("HeadPointer camera bridge", frame);
                if (Cv2.WaitKey(1) == 27) break;
            }

            var elapsed = Environment.TickCount64 - loopStart;
            var delay = TargetFrameIntervalMs - elapsed;
            if (delay > 0) cancellationToken.WaitHandle.WaitOne((int)delay);
        }
    }

    private sealed class BridgeOptions
    {
        public string Host { get; private init; } = "127.0.0.1";
        public int Port { get; private init; } = 11573;
        public int Camera { get; private init; }
        public bool InvertYaw { get; private init; }
        public bool ShowPreview { get; private init; }

        public static BridgeOptions Parse(string[] args)
        {
            string host = "127.0.0.1";
            var port = 11573;
            var camera = 0;
            var invertYaw = false;
            var showPreview = false;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--host":
                        host = RequireValue(args, ref i, "--host");
                        break;
                    case "--port":
                        port = int.Parse(RequireValue(args, ref i, "--port"));
                        break;
                    case "--camera":
                        camera = int.Parse(RequireValue(args, ref i, "--camera"));
                        break;
                    case "--invert-yaw":
                        invertYaw = true;
                        break;
                    case "--show-preview":
                        showPreview = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument: {args[i]}");
                }
            }

            return new BridgeOptions
            {
                Host = host,
                Port = port,
                Camera = camera,
                InvertYaw = invertYaw,
                ShowPreview = showPreview
            };
        }

        private static string RequireValue(string[] args, ref int index, string flag)
        {
            if (index + 1 >= args.Length) throw new ArgumentException($"{flag} requires a value.");
            index++;
            return args[index];
        }
    }
}
