# Linux live stream — done (source)

Capture path landed in `client/Services/Streaming/LinuxScreenCapturer.cs` + `ScreenCaptureService`.
Verify: `dotnet run -- --smoke-linux-capture` (Share once on first Wayland grant).
E2E WebRTC: re-login the Linux client (DeviceAuth must be valid), open `/live-stream`, start watch.
