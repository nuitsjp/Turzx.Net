# Turzx.Net

Unofficial .NET 10 library for the TURZX 9.2-inch USB display on Windows. Uses the Windows WinUSB driver, detects disconnection and reconnection, and sends baseline JPEG or PNG frames.

```csharp
using Turzx;

var device = (await TurzxDevices.ListAsync()).Single();
await using var session = new TurzxSession(device.DeviceId);
await session.StartAsync();
await session.WaitUntilReadyAsync();
await session.SendFrameAsync(TurzxFrame.FromPng(portraitPngBytes));
```

The input frame is 462×1920, rotated clockwise from the panel's 1920×462 landscape view. The session monitors the chosen device across USB reconnections. The application supplies a new frame when the session becomes ready again. See the [technical notes](https://github.com/nuitsjp/Turzx.Net/blob/main/docs/technical-notes.md) for verification details.

This is an unofficial library and is not affiliated with TURZX.
