using System.Threading.Channels;

namespace Turzx;

/// <summary>A long-lived session for one TURZX 9.2-inch USB display.</summary>
public sealed class TurzxSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly WindowsDeviceMonitor _monitor = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<TurzxStateChangedEventArgs> _events = Channel.CreateUnbounded<TurzxStateChangedEventArgs>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _eventWorker;
    private TaskCompletionSource _changed = NewSignal();
    private TurzxSessionState _state = TurzxSessionState.Created;
    private Exception? _error;
    private Task? _worker;
    private Task? _activeSend;
    private WinUsbConnection? _connection;
    private string? _path;
    private bool _sending;

    public TurzxSession(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        DeviceId = deviceId.ToUpperInvariant();
        if (!DeviceId.StartsWith("USB\\VID_1CBE&PID_0092\\", StringComparison.Ordinal))
            throw new ArgumentException("Expected a TURZX 9.2-inch USB device ID.", nameof(deviceId));
        _eventWorker = Task.Run(DispatchEventsAsync);
    }

    public string DeviceId { get; }
    public TurzxSessionState State { get { lock (_gate) return _state; } }
    public event EventHandler<TurzxStateChangedEventArgs>? StateChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state != TurzxSessionState.Created) throw new InvalidOperationException("The session has already started or been disposed.");
            _monitor.Start();
            SetState(TurzxSessionState.Disconnected);
            _worker = Task.Run(ProcessNotificationsAsync);
        }
        return Task.CompletedTask;
    }

    public async Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task change;
            lock (_gate)
            {
                if (_state == TurzxSessionState.Ready) return;
                if (_state == TurzxSessionState.Faulted) throw new InvalidOperationException("The TURZX session failed.", _error);
                if (_state == TurzxSessionState.Disposed) throw new ObjectDisposedException(nameof(TurzxSession));
                if (_state == TurzxSessionState.Created) throw new InvalidOperationException("Start the session first.");
                change = _changed.Task;
            }
            await change.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SendFrameAsync(TurzxFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        WinUsbConnection connection;
        Task send;
        lock (_gate)
        {
            if (_state == TurzxSessionState.Disposed) throw new ObjectDisposedException(nameof(TurzxSession));
            if (_state != TurzxSessionState.Ready || _connection is null) throw new InvalidOperationException("The TURZX session is not ready.");
            if (_sending) throw new InvalidOperationException("Another frame is being sent. Await it before sending the next frame.");
            connection = _connection;
            _sending = true;
            send = Task.Run(() => TurzxProtocol.SendFrame(connection, frame));
            _activeSend = send;
        }
        using var cancellation = cancellationToken.Register(connection.Abort);
        try
        {
            await send.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // An acknowledgement may have been lost. The application chooses its next frame.
            bool current;
            lock (_gate) current = _state == TurzxSessionState.Ready && ReferenceEquals(_connection, connection);
            if (current)
            {
                SetState(TurzxSessionState.Faulted, ex);
                _monitor.Signal();
            }
            throw;
        }
        finally
        {
            lock (_gate) { _sending = false; _activeSend = null; }
        }
    }

    public Task RetryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state != TurzxSessionState.Faulted) throw new InvalidOperationException("Retry is available only when the session is faulted.");
        }
        SetState(TurzxSessionState.Disconnected);
        _monitor.Signal();
        return Task.CompletedTask;
    }

    private async Task ProcessNotificationsAsync()
    {
        try
        {
            await foreach (uint action in _monitor.Signals.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try { await ReconcileAsync(action).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
                catch (Exception ex) { if (State != TurzxSessionState.Disposed) SetState(TurzxSessionState.Faulted, ex); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (State != TurzxSessionState.Disposed) SetState(TurzxSessionState.Faulted, ex); }
    }

    private async Task DispatchEventsAsync()
    {
        await foreach (var change in _events.Reader.ReadAllAsync().ConfigureAwait(false))
            if (StateChanged is { } handlers)
                foreach (EventHandler<TurzxStateChangedEventArgs> handler in handlers.GetInvocationList())
                    try { handler(this, change); } catch { /* Consumer callbacks cannot stop USB management. */ }
    }

    private async Task ReconcileAsync(uint action)
    {
        var matches = WindowsDeviceMonitor.Enumerate()
            .Where(path => WindowsDeviceMonitor.DeviceId(path).Equals(DeviceId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length > 1) throw new IOException($"Multiple interfaces match {DeviceId}.");
        var path = matches.SingleOrDefault();
        bool forceClose = action == 1 || path is null;
        WinUsbConnection? old = null;
        Task? active = null;
        lock (_gate)
        {
            if (_state == TurzxSessionState.Disposed) return;
            if (_connection is not null && (forceClose || !string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) || _state != TurzxSessionState.Ready))
            {
                old = _connection;
                _connection = null;
                _path = null;
                active = _activeSend;
            }
        }
        if (old is not null)
        {
            old.Abort();
            if (active is not null) { try { await active.ConfigureAwait(false); } catch { /* The sender receives the failure. */ } }
            old.Dispose();
        }
        if (path is null)
        {
            SetState(TurzxSessionState.Disconnected);
            return;
        }
        if (State == TurzxSessionState.Faulted) return;
        lock (_gate)
        {
            if (_state is TurzxSessionState.Ready or TurzxSessionState.Connecting && _connection is not null && _path == path) return;
        }
        SetState(TurzxSessionState.Connecting);
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            // An arrival notification can become stale before the device is openable.
            if (!WindowsDeviceMonitor.Enumerate().Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                SetState(TurzxSessionState.Disconnected);
                return;
            }
            WinUsbConnection? connection = null;
            try
            {
                connection = await Task.Run(() =>
                {
                    var opened = WinUsbConnection.Open(path);
                    try { TurzxProtocol.Synchronize(opened); return opened; }
                    catch { opened.Dispose(); throw; }
                }, _lifetime.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_state == TurzxSessionState.Disposed) { connection.Dispose(); return; }
                    _connection = connection;
                    _path = path;
                }
                SetState(TurzxSessionState.Ready);
                return;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                connection?.Dispose();
                last = ex;
                if (attempt < 2) await Task.Delay(200 * (attempt + 1), _lifetime.Token).ConfigureAwait(false);
            }
        }
        SetState(TurzxSessionState.Faulted, last);
    }

    private void SetState(TurzxSessionState state, Exception? error = null)
    {
        TurzxStateChangedEventArgs? change;
        lock (_gate)
        {
            if (_state == TurzxSessionState.Disposed || (_state == state && _error == error)) return;
            change = new(_state, state, error);
            _state = state;
            _error = error;
            _changed.TrySetResult();
            _changed = NewSignal();
            _events.Writer.TryWrite(change);
        }
    }

    public async ValueTask DisposeAsync()
    {
        WinUsbConnection? connection;
        Task? active;
        lock (_gate)
        {
            if (_state == TurzxSessionState.Disposed) return;
            connection = _connection;
            active = _activeSend;
        }
        SetState(TurzxSessionState.Disposed);
        _lifetime.Cancel();
        _monitor.Dispose();
        connection?.Abort();
        if (active is not null) { try { await active.ConfigureAwait(false); } catch { /* SendFrameAsync observes its own failure. */ } }
        if (_worker is not null) await _worker.ConfigureAwait(false);
        lock (_gate) { connection = _connection; _connection = null; }
        connection?.Dispose();
        _events.Writer.TryComplete();
        await _eventWorker.ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
