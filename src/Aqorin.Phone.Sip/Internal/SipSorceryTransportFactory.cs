using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Aqorin.Phone.Core.Diagnostics;
using Aqorin.Phone.Core.Model;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;

namespace Aqorin.Phone.Sip.Internal;

/// <summary>
/// Running tally of what went over one transport, so failure messages can say whether the registrar ever replied.
/// Updated from SIPSorcery's trace events; read from any thread.
/// </summary>
internal sealed class SipWireSummary
{
    private readonly object _gate = new();
    private int _sent;
    private int _received;
    private string? _lastSent;
    private string? _lastReceived;
    private DateTimeOffset? _lastReceivedAt;
    private string? _registrarEndPoint;

    public void Sent(SIPEndPoint remote, string summary)
    {
        lock (_gate)
        {
            _sent++;
            _lastSent = $"{summary} → {remote}";
            if (summary.StartsWith("REGISTER", StringComparison.Ordinal))
            {
                _registrarEndPoint = remote.ToString();
            }
        }
    }

    public void Received(SIPEndPoint remote, string summary)
    {
        lock (_gate)
        {
            _received++;
            _lastReceived = $"{summary} from {remote}";
            _lastReceivedAt = DateTimeOffset.Now;
        }
    }

    public string? RegistrarEndPoint
    {
        get
        {
            lock (_gate)
            {
                return _registrarEndPoint;
            }
        }
    }

    /// <summary>One sentence for status details, e.g. "Sent 2 SIP messages to udp:192.168.178.1:5060, received 1; last reply: 401 Unauthorized at 15:40:31."</summary>
    public string Describe()
    {
        lock (_gate)
        {
            var target = _registrarEndPoint ?? "the registrar";
            if (_received == 0)
            {
                return $"Sent {_sent} SIP message(s) to {target} and received no reply at all on this socket.";
            }

            return $"Sent {_sent} SIP message(s) to {target}, received {_received}; last reply: {_lastReceived} at {_lastReceivedAt:HH:mm:ss}.";
        }
    }
}

internal sealed class SipSorceryTransportHandle : ISipTransportHandle
{
    private readonly ILogger _logger;
    private bool _disposed;

    public SipSorceryTransportHandle(SIPTransport transport, string description, IPEndPoint? registrar, IPEndPoint? localContactEndPoint, ILogger logger)
    {
        Transport = transport;
        Description = description;
        Registrar = registrar;
        LocalContactEndPoint = localContactEndPoint;
        _logger = logger;
    }

    public SIPTransport Transport { get; }

    public string Description { get; }

    public IPEndPoint? Registrar { get; }

    public IPEndPoint? LocalContactEndPoint { get; }

    /// <summary>SIPSorcery outbound proxy: every request goes to the resolved registrar address (no per-request DNS).</summary>
    public SIPEndPoint? OutboundProxy => Registrar is null ? null : new SIPEndPoint(Transport.GetSIPChannels().FirstOrDefault()?.SIPProtocol ?? SIPProtocolsEnum.udp, Registrar);

    public SipWireSummary Wire { get; } = new();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Transport.Shutdown();
            Transport.Dispose();
            _logger.LogInformation("SIP transport closed ({Description}).", Description);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while closing the SIP transport.");
        }
    }
}

/// <summary>Creates a SIPSorcery transport with one UDP or TCP channel bound to all IPv4 interfaces.</summary>
internal sealed class SipSorceryTransportFactory(ILogger<SipSorceryTransportFactory> logger, SipDiagnosticsOptions diagnostics) : ISipTransportFactory
{
    public ISipTransportHandle Create(SipAccountSettings settings, IPEndPoint? registrar)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var transport = new SIPTransport();
        var endpoint = new IPEndPoint(IPAddress.Any, settings.LocalPort);
        try
        {
            SIPChannel channel = settings.Transport == SipTransport.Tcp
                ? new SIPTCPChannel(endpoint)
                : new SIPUDPChannel(endpoint);
            transport.AddSIPChannel(channel);
            var localContact = LocalContactEndPoint(channel, registrar);
            var description = $"{settings.Transport.ToString().ToUpperInvariant()} {channel.ListeningSIPEndPoint}" + (registrar is null ? string.Empty : $" → {registrar}");
            var handle = new SipSorceryTransportHandle(transport, description, registrar, localContact, logger);
            AttachTracing(transport, handle.Wire);
            AttachOptionsResponder(transport);
            logger.LogInformation("SIP transport opened ({Description}).", description);
            return handle;
        }
        catch (Exception ex)
        {
            transport.Shutdown();
            transport.Dispose();
            throw new InvalidOperationException(
                $"Could not open a local {settings.Transport} SIP socket on port {(settings.LocalPort == 0 ? "(auto)" : settings.LocalPort)}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Answers out-of-dialog OPTIONS pings (keep-alive / reachability probes some PBXes send) with 200 OK so the
    /// registrar keeps considering the phone reachable. In-dialog requests are handled by the user agent.
    /// </summary>
    private void AttachOptionsResponder(SIPTransport transport)
    {
        transport.SIPTransportRequestReceived += async (local, remote, request) =>
        {
            if (request.Method != SIPMethodsEnum.OPTIONS || request.Header.To?.ToTag is not null)
            {
                return;
            }

            try
            {
                var ok = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
                ok.Header.Allow = "INVITE, ACK, CANCEL, BYE, OPTIONS";
                ok.Header.Accept = "application/sdp";
                await transport.SendResponseAsync(ok).ConfigureAwait(false);
                logger.LogDebug("Answered OPTIONS from {Remote}.", remote);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Answering OPTIONS failed.");
            }
        };
    }

    private void AttachTracing(SIPTransport transport, SipWireSummary wire)
    {
        transport.SIPRequestInTraceEvent += (local, remote, req) => Trace("<-", remote, req.StatusLine, req.ToString(), wire);
        transport.SIPRequestOutTraceEvent += (local, remote, req) => Trace("->", remote, req.StatusLine, req.ToString(), wire);
        transport.SIPResponseInTraceEvent += (local, remote, resp) => Trace("<-", remote, resp.ShortDescription, resp.ToString(), wire);
        transport.SIPResponseOutTraceEvent += (local, remote, resp) => Trace("->", remote, resp.ShortDescription, resp.ToString(), wire);
        transport.SIPBadRequestInTraceEvent += (local, remote, message, field, raw) =>
            logger.LogWarning("Bad SIP request from {Remote}: {Message} ({Field})", remote, message, field);
        transport.SIPBadResponseInTraceEvent += (local, remote, message, field, raw) =>
            logger.LogWarning("Bad SIP response from {Remote}: {Message} ({Field})", remote, message, field);
    }

    private static IPEndPoint? LocalContactEndPoint(SIPChannel channel, IPEndPoint? registrar)
    {
        var port = channel.ListeningEndPoint?.Port ?? channel.Port;
        if (port <= 0)
        {
            port = channel.ListeningSIPEndPoint?.Port ?? 0;
        }

        var address = ContactAddressFor(registrar);
        return address is null || port <= 0 ? null : new IPEndPoint(address, port);
    }

    private static IPAddress? ContactAddressFor(IPEndPoint? registrar)
    {
        if (registrar is null)
        {
            return null;
        }

        if (IPAddress.IsLoopback(registrar.Address))
        {
            return IPAddress.Loopback;
        }

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(registrar);
            return ((IPEndPoint)socket.LocalEndPoint!).Address;
        }
        catch
        {
            return NetworkInterface
                .GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address)
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
        }
    }

    private void Trace(string direction, SIPEndPoint remote, string summary, string fullMessage, SipWireSummary wire)
    {
        var line = FirstLine(summary);
        if (direction == "->")
        {
            wire.Sent(remote, line);
        }
        else
        {
            wire.Received(remote, line);
        }

        if (diagnostics.SipTraceEnabled)
        {
            // Full message at Debug (redacted: Authorization headers, digest responses).
            logger.LogDebug("SIP {Direction} {Remote}\n{Message}", direction, remote, SensitiveDataRedactor.Redact(fullMessage));
        }
        else
        {
            // Always visible in the diagnostics panel: one line per message, no headers or bodies.
            logger.LogInformation("SIP {Direction} {Remote}  {Summary}", direction, remote, line);
        }
    }

    private static string FirstLine(string text)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        var line = index >= 0 ? text[..index] : text;
        return line.Replace(" SIP/2.0", string.Empty, StringComparison.Ordinal).Replace("SIP/2.0 ", string.Empty, StringComparison.Ordinal).Trim();
    }
}
