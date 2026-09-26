namespace Turzx;

public static class TurzxDevices
{
    public static Task<IReadOnlyList<TurzxDeviceInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TurzxDeviceInfo> devices = WindowsDeviceMonitor.Enumerate()
            .Select(path => new TurzxDeviceInfo(WindowsDeviceMonitor.DeviceId(path), "TURZX 9.2-inch", 1920, 462))
            .ToArray();
        return Task.FromResult(devices);
    }
}
