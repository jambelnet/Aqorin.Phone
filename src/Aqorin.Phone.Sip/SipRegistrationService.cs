using System.Net.NetworkInformation;
using Aqorin.Phone.Core.Abstractions;
using Aqorin.Phone.Core.Model;
using Aqorin.Phone.Core.Retry;
using Aqorin.Phone.Core.StateMachines;
using Aqorin.Phone.Sip.Internal;
using Microsoft.Extensions.Logging;

namespace Aqorin.Phone.Sip;

/// <summary>
/// Registration lifecycle on top of the SIP stack:
/// opens the transport, runs REGISTER, retries temporary failures with bounded exponential backoff,
/// refreshes before expiry (delegated to the stack's timer), re-registers on network changes and
/// sends a de-registration on <see cref="UnregisterAsync"/>.
/// </summary>
public sealed class SipRegistrationService : ISipRegistrationService
{
    private readonly ISipTransportFactory _transportFactory;
    private readonly ISipRegistrationClientFactory _clientFactory;
    private readonly IRegistrarResolver _resolver;
    private readonly SipSessionContext _context;
    private readonly ExponentialBackoffPolicy _backoff;
    private readonly TimeSpan _attemptTimeout;
    private readonly ILogger<SipRegistrationService> _logger;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _gate = new();

    private RegistrationStatus _status = RegistrationStatus.Disconnected;
    private System.Net.IPEndPoint? _registrar;
    private SipAccount? _account;
    private ISipRegistrationClient? _client;
    private CancellationTokenSource? _lifetime;
    private int _failedAttempts;
    private bool _networkHooked;
    private bool _disposed;

    internal SipRegistrationService(
        ISipTransportFactory transportFactory,
        ISipRegistrationClientFactory clientFactory,
        SipSessionContext context,
        ILogger<SipRegistrationService> logger,
        ExponentialBackoffPolicy? backoff = null,
        TimeSpan? attemptTimeout = null,
        IRegistrarResolver? resolver = null)
    {
        _transportFactory = transportFactory;
        _clientFactory = clientFactory;
        _resolver = resolver ?? new SystemDnsRegistrarResolver();
        _registrar = null;
        _context = context;
        _logger = logger;
        _backoff = backoff ?? ExponentialBackoffPolicy.RegistrationDefault;
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>Extra time granted beyond the attempt timeout before an attempt without any outcome is treated as failed.</summary>
    internal TimeSpan GuardGrace { get; set; } = TimeSpan.FromSeconds(10);

    public RegistrationStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public SipAccountSettings? CurrentAccount => _account?.Settings;

    public event EventHandler<RegistrationStatus>? StatusChanged;

    /// <summary>Set to true to watch <see cref="NetworkChange"/> events and re-register when addresses change.</summary>
    public bool ReRegisterOnNetworkChange { get; set; } = true;

    public async Task RegisterAsync(SipAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var validation = SipAccountValidator.Validate(account.Settings, account.Password);
        if (!validation.IsValid)
        {
            throw new ArgumentException(string.Join(" ", validation.Errors), nameof(account));
        }

        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (!RegistrationStateMachine.CanRegister(_status.State))
                {
                    throw new InvalidRegistrationOperationException("register", _status.State);
                }
            }

            _account = account;
            _failedAttempts = 0;
            _lifetime?.Dispose();
            _lifetime = new CancellationTokenSource();
            var aor = account.Settings.AddressOfRecord;
            Publish(new RegistrationStatus
            {
                State = RegistrationState.Registering,
                Message = $"Registering {aor}…",
                AddressOfRecord = aor,
            });

            // Resolve first so host-name mistakes are caught before any credentials-related traffic is sent.
            var host = account.Settings.Registrar.Trim();
            var addresses = await _resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
            var (address, resolveError) = RegistrarResolution.Choose(host, addresses);
            if (address is null)
            {
                _logger.LogWarning("Registrar {Host} not usable: {Error}", host, resolveError);
                Publish(new RegistrationStatus
                {
                    State = RegistrationState.Failed,
                    Message = RegistrarResolution.IsFritzBoxName(host) && addresses.Length > 0
                        ? $"{host} does not point to your FRITZ!Box on this network. Use its LAN IP address (e.g. 192.168.178.1)."
                        : $"Cannot resolve host {host}.",
                    Detail = resolveError,
                    AddressOfRecord = aor,
                    FailedAttempts = 1,
                });
                return;
            }

            _registrar = new System.Net.IPEndPoint(address, account.Settings.Port);
            _logger.LogInformation("Registrar {Host} resolved to {EndPoint}.", host, _registrar);

            ISipTransportHandle transport;
            try
            {
                transport = _transportFactory.Create(account.Settings, _registrar);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not open the SIP transport.");
                Publish(new RegistrationStatus
                {
                    State = RegistrationState.Failed,
                    Message = "Could not open a local SIP socket.",
                    Detail = ex.Message,
                    AddressOfRecord = aor,
                });
                return;
            }

            _context.Open(transport, account);
            HookNetworkEvents();
            await AttemptAsync(_lifetime.Token, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    public async Task UnregisterAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RegistrationState state;
            lock (_gate)
            {
                state = _status.State;
                if (!RegistrationStateMachine.CanUnregister(state))
                {
                    throw new InvalidRegistrationOperationException("unregister", state);
                }
            }

            await ShutdownAsync(sendUnregister: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnhookNetworkEvents();
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_status.State != RegistrationState.Disconnected)
            {
                await ShutdownAsync(sendUnregister: true, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during registration service shutdown.");
        }
        finally
        {
            _operation.Release();
            _operation.Dispose();
            _lifetime?.Dispose();
        }
    }

    // ------------------------------------------------------------------------------------------------------------------

    private async Task AttemptAsync(CancellationToken lifetime, CancellationToken caller)
    {
        var account = _account!;
        var transport = _context.Transport;
        if (transport is null || lifetime.IsCancellationRequested)
        {
            return;
        }

        var aor = account.Settings.AddressOfRecord;
        var tcs = new TaskCompletionSource<RegistrationEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = _clientFactory.Create(transport, account, _attemptTimeout);
        client.Completed += evt =>
        {
            if (!tcs.TrySetResult(evt))
            {
                // Later events (refresh results) after the first outcome.
                OnLaterEvent(client, evt);
            }
        };

        lock (_gate)
        {
            _client?.Dispose();
            _client = client;
        }

        try
        {
            client.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registration client failed to start.");
            tcs.TrySetResult(new RegistrationEvent(RegistrationOutcome.TemporaryFailure, ex.Message, null, null));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, caller);
        var guard = _attemptTimeout + GuardGrace;
        RegistrationEvent outcome;
        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(guard, linked.Token)).ConfigureAwait(false);
            outcome = completed == tcs.Task
                ? await tcs.Task.ConfigureAwait(false)
                : new RegistrationEvent(RegistrationOutcome.TemporaryFailure, $"No response from {account.Settings.HostPort} within {guard.TotalSeconds:0}s.", null, null);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        HandleOutcome(client, outcome, lifetime);
    }

    private void OnLaterEvent(ISipRegistrationClient client, RegistrationEvent evt)
    {
        var lifetime = _lifetime?.Token ?? CancellationToken.None;
        if (lifetime.IsCancellationRequested)
        {
            return;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_client, client))
            {
                return;
            }
        }

        HandleOutcome(client, evt, lifetime);
    }

    private void HandleOutcome(ISipRegistrationClient client, RegistrationEvent evt, CancellationToken lifetime)
    {
        var account = _account;
        if (account is null || lifetime.IsCancellationRequested)
        {
            return;
        }

        var aor = account.Settings.AddressOfRecord;
        switch (evt.Outcome)
        {
            case RegistrationOutcome.Success:
                _failedAttempts = 0;
                _context.IsRegistered = true;
                var expiry = evt.ExpirySeconds ?? account.Settings.RegistrationExpirySeconds;
                Publish(new RegistrationStatus
                {
                    State = RegistrationState.Registered,
                    Message = $"Registered as {aor}",
                    Detail = $"Registration expires in {expiry}s; refresh scheduled automatically. Listening on {_context.Transport?.Description ?? "the SIP transport"}.",
                    AddressOfRecord = aor,
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiry),
                });
                break;

            case RegistrationOutcome.AuthenticationFailure:
                _context.IsRegistered = false;
                _failedAttempts++;
                lock (_gate)
                {
                    if (ReferenceEquals(_client, client))
                    {
                        _client = null;
                    }
                }

                client.Dispose();
                Publish(new RegistrationStatus
                {
                    State = RegistrationState.Failed,
                    Message = evt.StatusCode is 404 ? "Registration rejected: user not found." : "Registration rejected: wrong username or password.",
                    Detail = evt.Detail,
                    AddressOfRecord = aor,
                    FailedAttempts = _failedAttempts,
                    IsAuthenticationFailure = true,
                });
                ReleaseTransport();
                break;

            case RegistrationOutcome.ResolutionFailure:
            case RegistrationOutcome.TemporaryFailure:
                _context.IsRegistered = false;
                _failedAttempts++;
                lock (_gate)
                {
                    if (ReferenceEquals(_client, client))
                    {
                        _client = null;
                    }
                }

                client.Dispose();
                ScheduleRetry(evt, lifetime);
                break;
        }
    }

    private void ScheduleRetry(RegistrationEvent evt, CancellationToken lifetime)
    {
        var account = _account!;
        var aor = account.Settings.AddressOfRecord;
        var friendly = evt.Outcome == RegistrationOutcome.ResolutionFailure
            ? $"Cannot resolve host {account.Settings.Registrar}."
            : $"Registrar {account.Settings.HostPort} did not respond.";
        var isFritzName = RegistrarResolution.IsFritzBoxName(account.Settings.Registrar);
        var hint = evt.Outcome == RegistrationOutcome.ResolutionFailure
            ? isFritzName
                ? "Use the FRITZ!Box LAN IP (e.g. 192.168.178.1) if fritz.box is not resolvable on this network (VPN, other DNS)."
                : "Use the router or PBX LAN IP address if its host name is not resolvable on this network."
            : isFritzName
                ? "If the FRITZ!Box answered earlier and is now silent, it has probably blocked this phone for a while after failed login attempts (FRITZ!Box system log); also check that no VPN or firewall blocks SIP to the LAN."
                : "Check that the registrar allows this SIP account from your computer and that no VPN or firewall blocks UDP/TCP 5060 to the LAN.";
        var detail = string.IsNullOrWhiteSpace(evt.Detail) ? hint : evt.Detail + Environment.NewLine + hint;

        if (!_backoff.TryGetDelay(_failedAttempts, out var delay))
        {
            Publish(new RegistrationStatus
            {
                State = RegistrationState.Failed,
                Message = friendly + " Gave up after " + _failedAttempts + " attempts.",
                Detail = detail,
                AddressOfRecord = aor,
                FailedAttempts = _failedAttempts,
            });
            ReleaseTransport();
            return;
        }

        var retryAt = DateTimeOffset.UtcNow + delay;
        Publish(new RegistrationStatus
        {
            State = RegistrationState.Registering,
            Message = $"{friendly} Retrying in {Math.Round(delay.TotalSeconds)}s (attempt {_failedAttempts + 1} of {_backoff.MaxAttempts + 1})…",
            Detail = detail,
            AddressOfRecord = aor,
            NextRetryAt = retryAt,
            FailedAttempts = _failedAttempts,
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, lifetime).ConfigureAwait(false);
                await _operation.WaitAsync(lifetime).ConfigureAwait(false);
                try
                {
                    if (lifetime.IsCancellationRequested || _status.State != RegistrationState.Registering)
                    {
                        return;
                    }

                    RecycleTransportIfIdle();
                    await AttemptAsync(lifetime, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _operation.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Registration retry failed unexpectedly.");
            }
        }, CancellationToken.None);
    }

    private async Task ShutdownAsync(bool sendUnregister, CancellationToken cancellationToken)
    {
        var aor = _account?.Settings.AddressOfRecord;
        Publish(new RegistrationStatus { State = RegistrationState.Unregistering, Message = "Unregistering…", AddressOfRecord = aor });

        _lifetime?.Cancel();
        ISipRegistrationClient? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
        }

        if (client is not null)
        {
            try
            {
                if (sendUnregister && client.IsRegistered)
                {
                    var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    client.Removed += () => removed.TrySetResult();
                    client.Stop(sendUnregister: true);
                    await Task.WhenAny(removed.Task, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)).ConfigureAwait(false);
                }
                else
                {
                    client.Stop(sendUnregister: false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while stopping the registration client.");
            }
            finally
            {
                client.Dispose();
            }
        }

        ReleaseTransport();
        _account = null;
        Publish(new RegistrationStatus { State = RegistrationState.Disconnected, Message = "Not registered", AddressOfRecord = aor });
    }

    /// <summary>
    /// A registrar that "does not respond" is most often a local socket that stopped receiving (firewall state,
    /// network change, a stalled receive loop). Before retrying, open a fresh transport — unless a call is in progress,
    /// which would be torn down with the socket.
    /// </summary>
    private void RecycleTransportIfIdle()
    {
        var account = _account;
        if (account is null || _context.CallInProgress || _context.Transport is null)
        {
            return;
        }

        try
        {
            var fresh = _transportFactory.Create(account.Settings, _registrar);
            _context.ReplaceTransport(fresh);
            _logger.LogInformation("Re-opened the SIP transport before retrying registration ({Description}).", fresh.Description);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not re-open the SIP transport; retrying on the existing one.");
        }
    }

    private void ReleaseTransport()
    {
        _context.IsRegistered = false;
        try
        {
            _context.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while closing the SIP transport.");
        }
    }

    private void Publish(RegistrationStatus status)
    {
        lock (_gate)
        {
            if (_status.State != status.State)
            {
                RegistrationStateMachine.EnsureTransition(_status.State, status.State);
            }

            _status = status;
        }

        _logger.LogInformation("Registration: {State} — {Message}", status.State, status.Message);
        StatusChanged?.Invoke(this, status);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Network changes: restart the registration client (same transport) after a short debounce.

    private CancellationTokenSource? _networkDebounce;

    private void HookNetworkEvents()
    {
        if (_networkHooked || !ReRegisterOnNetworkChange)
        {
            return;
        }

        try
        {
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            _networkHooked = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Network change notifications are not available on this platform.");
        }
    }

    private void UnhookNetworkEvents()
    {
        if (!_networkHooked)
        {
            return;
        }

        try
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to unhook network change notifications.");
        }

        _networkHooked = false;
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            OnNetworkChanged(sender, e);
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        var lifetime = _lifetime?.Token;
        if (lifetime is null || lifetime.Value.IsCancellationRequested || _status.State != RegistrationState.Registered)
        {
            return;
        }

        _networkDebounce?.Cancel();
        var debounce = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Value);
        _networkDebounce = debounce;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), debounce.Token).ConfigureAwait(false);
                _logger.LogInformation("Network change detected; refreshing registration.");
                await _operation.WaitAsync(debounce.Token).ConfigureAwait(false);
                try
                {
                    if (_status.State != RegistrationState.Registered)
                    {
                        return;
                    }

                    ISipRegistrationClient? old;
                    lock (_gate)
                    {
                        old = _client;
                        _client = null;
                    }

                    old?.Stop(sendUnregister: false);
                    old?.Dispose();
                    Publish(new RegistrationStatus
                    {
                        State = RegistrationState.Registering,
                        Message = "Network changed; re-registering…",
                        AddressOfRecord = _account?.Settings.AddressOfRecord,
                    });
                    await AttemptAsync(lifetime.Value, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _operation.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Re-registration after network change failed.");
            }
        }, CancellationToken.None);
    }
}
