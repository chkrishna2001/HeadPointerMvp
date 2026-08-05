# OpenSeeFace bridge contract

This folder intentionally defines a narrow integration contract rather than embedding OpenSeeFace's Python/runtime dependencies in the Windows tray application.

Run OpenSeeFace with its webcam tracker, then use a tiny local adapter to convert its face-pose output into UDP datagrams sent to `127.0.0.1:11573`:

```json
{ "yaw": -18.4, "pitch": 2.1, "roll": 0.3 }
```

Requirements for an adapter:

- Emit at least 15 pose packets per second while one face is confidently tracked.
- Emit degrees, not radians; set yaw negative for a left turn and positive for a right turn.
- Do not send camera frames, landmarks, identities, or data to a network address.
- Bind/sends to `127.0.0.1` only.

OpenSeeFace's data format and launch flags vary between releases. Keep that compatibility code inside the adapter, where it can be updated separately. For a direct C# MediaPipe integration, implement `IHeadPoseSource`, publish the same `HeadPose` event, and leave all other application code unchanged.
