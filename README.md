# Turzx.Net

Turzx.Net is an unofficial .NET 10 library for controlling the TURZX 9.2-inch display over Windows WinUSB.

Install from NuGet: `dotnet add package Turzx.Net`.

Requirements: Windows, the .NET 10 SDK, and a TURZX 9.2-inch device configured for WinUSB.

```csharp
using Turzx;

var devices = await TurzxDevices.ListAsync();
var device = devices.Single();
await using var session = new TurzxSession(device.DeviceId);
await session.StartAsync();
await session.WaitUntilReadyAsync();
await session.SendFrameAsync(TurzxFrame.FromPng(portraitPngBytes));
```

`portraitPngBytes` must contain a 462x1920 PNG rotated clockwise for the display.

See the [detailed technical documentation](docs/technical-notes.md) for protocol notes, reconnection behavior, benchmarks, and development commands.
