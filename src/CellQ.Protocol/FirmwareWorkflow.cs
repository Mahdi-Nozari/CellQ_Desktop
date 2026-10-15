namespace CellQ.Protocol;

public enum FirmwarePhase { Ready, CheckingReleases, Downloading, Verifying, AwaitingConfirmation, Preparing, Installing, Reconnecting, Complete, Cancelled, Error }
public sealed record FirmwareProgress(FirmwarePhase Phase, string Message, double? Percentage = null);
public sealed record FirmwareConfirmation(string Port, string InstalledVersion, string ReleaseVersion, string Sha256);
public sealed record FirmwareWorkflowOptions
{
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int ReconnectAttempts { get; init; } = 5;
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);
}
public interface IFirmwareUploader
{
    // Implementations must not return/throw until the process has released the port.
    Task ValidateAsync(CancellationToken token);
    Task UploadAsync(string port, string hexPath, Action<FirmwareProgress> progress);
}

public sealed partial class DeviceConnectionService
{
    public async Task<bool> InstallFirmwareAsync(VerifiedFirmware package, IFirmwareUploader uploader,
        Func<FirmwareConfirmation, CancellationToken, Task<bool>> confirm, Action<FirmwareProgress> progress,
        CancellationToken token = default, FirmwareWorkflowOptions? options = null)
    {
        options ??= new();
        if (options.ReconnectAttempts is < 1 or > 20 || options.ReconnectDelay < TimeSpan.Zero || options.CommandTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        await _operations.WaitAsync(token).ConfigureAwait(false);
        Session? session = _session;
        bool entered = false, entrySent = false, handoff = false, responseUncertain = false;
        string? port = Snapshot.PortName;
        using var workflow = CancellationTokenSource.CreateLinkedTokenSource(token);
        Exception? failure = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (session is null || Snapshot.Phase != ConnectionPhase.Connected || port is null) throw new IOException("Connect your CellQ before installing firmware.");
            lock (_sync) _workflowCancellation = workflow;
            SetState(session, Snapshot with { WorkflowActive = true });
            await uploader.ValidateAsync(workflow.Token).ConfigureAwait(false);
            await using var lockedImage = await package.LockAsync(workflow.Token).ConfigureAwait(false);
            var hello = await IdentifyAsync(session).ConfigureAwait(false); AcceptIdentity(session, port, hello);
            CheckTargetVersion(hello, package.Release.Version);
            progress(new(FirmwarePhase.AwaitingConfirmation, "Confirm the update to continue."));
            if (!await confirm(new(port, hello.FirmwareVersion, package.Release.Version, package.Sha256), workflow.Token).ConfigureAwait(false))
            { progress(new(FirmwarePhase.Cancelled, "Update cancelled. The device was not prepared or restarted.")); return false; }
            workflow.Token.ThrowIfCancellationRequested();
            var fresh = await IdentifyAsync(session).ConfigureAwait(false); AcceptIdentity(session, port, fresh);
            CheckTargetVersion(fresh, package.Release.Version);
            if (fresh.FirmwareVersion != hello.FirmwareVersion) throw new IOException("The device version changed after review. Review the update again.");
            var client = new LogSession(this, session, new() { CommandTimeout = options.CommandTimeout });
            progress(new(FirmwarePhase.Preparing, "Preparing your CellQ for the update."));
            if (fresh.Mode == DeviceMode.Maintenance)
            {
                // EXIT is documented to recover failed FWPREP; do not assume SD commands still work.
                entered = true; Interlocked.Exchange(ref _maintenanceMayRemain, 1);
                await client.OkAsync("EXIT", CancellationToken.None).ConfigureAwait(false); entered = false;
                fresh = await IdentifyAsync(session).ConfigureAwait(false); AcceptIdentity(session, port, fresh);
                CheckTargetVersion(fresh, package.Release.Version);
                if (fresh.Mode == DeviceMode.Maintenance) throw new IOException("The device remained in Maintenance mode after EXIT.");
            }
            workflow.Token.ThrowIfCancellationRequested(); entrySent = true;
            await client.OkAsync("ENTER", CancellationToken.None).ConfigureAwait(false);
            entered = true; Interlocked.Exchange(ref _maintenanceMayRemain, 1);
            SetState(session, Snapshot with { Mode = DeviceMode.Maintenance, MaintenanceOwned = true });
            workflow.Token.ThrowIfCancellationRequested();
            await client.OkAsync("FWPREP", CancellationToken.None).ConfigureAwait(false);
            workflow.Token.ThrowIfCancellationRequested();
            await StopAsync(session).ConfigureAwait(false); session = null; handoff = true;
            SetState(null, new(ConnectionPhase.Updating, port, hello.FirmwareVersion, WorkflowActive: true, MaintenanceOwned: true));
            progress(new(FirmwarePhase.Installing, "Installing firmware. Keep USB and device power connected."));
            try { await uploader.UploadAsync(port, package.Path, progress).ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            // From reset onward, ordinary cancellation cannot interrupt flashing or abandon verification.
            progress(new(FirmwarePhase.Reconnecting, failure is null ? "Reconnecting to check the installed version." : "Installation failed. Reconnecting to check the device and recover maintenance if possible."));
            session = await ReconnectFirmwareAsync(port, options).ConfigureAwait(false);
            if (Snapshot.Mode == DeviceMode.Maintenance)
            {
                var recovery = new LogSession(this, session, new() { CommandTimeout = options.CommandTimeout });
                await recovery.OkAsync("EXIT", CancellationToken.None).ConfigureAwait(false);
                AcceptIdentity(session, port, await IdentifyAsync(session).ConfigureAwait(false));
                if (Snapshot.Mode == DeviceMode.Maintenance) throw new IOException("The device did not leave Maintenance mode.");
            }
            entered = false;
            if (failure is not null) throw new IOException("The uploader reported a failure. The update is not complete; check technical details before a new confirmed attempt.", failure);
            if (Snapshot.FirmwareVersion != package.Release.Version) throw new IOException($"Installed version could not be confirmed. Expected {package.Release.Version}; device reported {Snapshot.FirmwareVersion}.");
            if (Snapshot.Mode == DeviceMode.Test) throw new IOException("The returning device reports an active test. The update result needs review; no test was stopped.");
            Interlocked.Exchange(ref _maintenanceMayRemain, 0);
            progress(new(FirmwarePhase.Complete, $"Update complete. Your CellQ is running firmware {package.Release.Version}."));
            return true;
        }
        catch (Exception error)
        {
            failure ??= error;
            responseUncertain = error is TimeoutException or TransferIntegrityException or ProtocolException;
            if (entrySent && !entered) Interlocked.Exchange(ref _maintenanceMayRemain, 1);
            progress(new(error is OperationCanceledException ? FirmwarePhase.Cancelled : FirmwarePhase.Error, error is OperationCanceledException ? "Update stopped before installation. Returning the device to normal when possible." : error.Message));
            throw;
        }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                if (session is not null && !entered && !entrySent && (responseUncertain || session.Cancellation.IsCancellationRequested))
                    await FailOrCancelAsync(session, failure ?? new IOException("Device identification failed.")).ConfigureAwait(false);
                if (session is not null && entered && !handoff)
                {
                    if (responseUncertain || session.Cancellation.IsCancellationRequested) throw new IOException("Preparation was not confirmed. Reconnect and choose Return to normal if Maintenance mode is reported.");
                    var cleanup = new LogSession(this, session, new() { CommandTimeout = options.CommandTimeout });
                    await cleanup.OkAsync("EXIT", CancellationToken.None).ConfigureAwait(false);
                    AcceptIdentity(session, port, await IdentifyAsync(session).ConfigureAwait(false));
                    if (Snapshot.Mode == DeviceMode.Maintenance) throw new IOException("The device did not leave Maintenance mode.");
                }
                else if (session is not null && entrySent && !entered && !handoff)
                {
                    if (responseUncertain || session.Cancellation.IsCancellationRequested) throw new IOException("Maintenance entry is uncertain. Reconnect and check device status.");
                    // A denial code has no documented meaning; observe the actual mode.
                    AcceptIdentity(session, port, await IdentifyAsync(session).ConfigureAwait(false));
                    if (Snapshot.Mode == DeviceMode.Maintenance)
                    {
                        await new LogSession(this, session, new() { CommandTimeout = options.CommandTimeout }).OkAsync("EXIT", CancellationToken.None).ConfigureAwait(false);
                        AcceptIdentity(session, port, await IdentifyAsync(session).ConfigureAwait(false));
                        if (Snapshot.Mode == DeviceMode.Maintenance) throw new IOException("The device did not leave Maintenance mode.");
                    }
                }
            }
            catch (Exception error)
            {
                cleanupFailure = new MaintenanceRecoveryException("The device could not be confirmed back in normal mode. Reconnect and choose Return to normal. " + error.Message, error);
                Interlocked.Exchange(ref _maintenanceMayRemain, 1); Emit("Error", cleanupFailure.Message);
                if (session is not null) try { await FailOrCancelAsync(session, cleanupFailure).ConfigureAwait(false); } catch (Exception closeError) { Emit("Error", closeError.Message); }
            }
            lock (_sync) _workflowCancellation = null;
            var current = _session;
            SetState(current, Snapshot with { WorkflowActive = false, MaintenanceOwned = false });
            _operations.Release();
            if (cleanupFailure is not null && failure is null) throw cleanupFailure;
        }
    }
    private static void CheckTargetVersion(HelloResponse hello, string release)
    {
        if (hello.Mode == DeviceMode.Test) throw new MaintenanceDeniedException("A test is running or paused. Finish it on the device before updating firmware.");
        if (!GitHubFirmwareReleases.TryVersion(hello.FirmwareVersion, out var installed) || !GitHubFirmwareReleases.TryVersion(release, out var proposed)) throw new IOException("A stable firmware version could not be established. No update is allowed.");
        if (proposed! <= installed!) throw new IOException("The selected release is already installed or older. Reinstallation and downgrades are unavailable.");
    }
    private async Task<Session> ReconnectFirmwareAsync(string port, FirmwareWorkflowOptions options)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < options.ReconnectAttempts; attempt++)
        {
            await Task.Delay(options.ReconnectDelay).ConfigureAwait(false);
            var session = new Session(createTransport(), CancellationToken.None);
            lock (_sync) _session = session;
            SetState(session, new(ConnectionPhase.Updating, port, WorkflowActive: true));
            try
            {
                await session.Transport.OpenAsync(port, new SerialConfiguration(), session.Cancellation.Token).ConfigureAwait(false);
                session.Reader = Task.Run(() => ReadLoopAsync(session));
                AcceptIdentity(session, port, await IdentifyAsync(session).ConfigureAwait(false));
                return session;
            }
            catch (Exception error)
            {
                last = error; await StopAsync(session).ConfigureAwait(false); // Failed close prohibits retry/new owner.
                SetState(null, new(ConnectionPhase.Updating, port, WorkflowActive: true));
            }
        }
        SetState(null, new(ConnectionPhase.Error, port, Error: "Could not reconnect after firmware installation.", WorkflowActive: true));
        throw new IOException("The device did not reconnect. The update is unconfirmed. Check USB/power and reconnect the same device; no upload was retried.", last);
    }
}
