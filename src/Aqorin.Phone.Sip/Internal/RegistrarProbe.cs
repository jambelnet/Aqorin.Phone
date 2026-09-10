using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Aqorin.Phone.Sip.Internal;

/// <summary>
/// When the registrar never answers over UDP, this explains why by trying the same SIP request over TCP:
/// <list type="bullet">
/// <item>TCP port closed → the host is not a SIP registrar / wrong port.</item>
/// <item>TCP connects and the registrar answers → UDP is filtered; switch the transport to TCP.</item>
/// <item>TCP connects but the connection is aborted the moment SIP is sent → a local security product
/// (endpoint firewall / VPN client with application control) blocks SIP on this computer.</item>
/// <item>TCP connects and stays silent → the box drops SIP from this client (e.g. temporary lock-out).</item>
/// </list>
/// Nothing sensitive is sent: an OPTIONS request without credentials.
/// </summary>
internal static class RegistrarProbe
{
    public static async Task<string> RunAsync(IPEndPoint registrar, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var token = cts.Token;

        using var client = new TcpClient(registrar.AddressFamily);
        try
        {
            await client.ConnectAsync(registrar.Address, registrar.Port, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return $"Probe: TCP connection to {registrar} timed out — the host is unreachable or drops SIP traffic.";
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return $"Probe: nothing listens on TCP port {registrar.Port} at {registrar.Address} - check the registrar address and SIP port.";
        }
        catch (SocketException ex)
        {
            return $"Probe: TCP connection to {registrar} failed ({ex.SocketErrorCode}).";
        }

        var local = (IPEndPoint)client.Client.LocalEndPoint!;
        var options =
            $"OPTIONS sip:{registrar.Address} SIP/2.0\r\n" +
            $"Via: SIP/2.0/TCP {local.Address}:{local.Port};branch=z9hG4bK{Guid.NewGuid():N};rport\r\n" +
            "Max-Forwards: 70\r\n" +
            $"From: <sip:probe@{registrar.Address}>;tag={Guid.NewGuid().ToString("N")[..8]}\r\n" +
            $"To: <sip:{registrar.Address}>\r\n" +
            $"Call-ID: {Guid.NewGuid():N}@probe\r\n" +
            "CSeq: 1 OPTIONS\r\n" +
            "User-Agent: Aqorin.Phone-probe\r\n" +
            "Content-Length: 0\r\n\r\n";

        try
        {
            var stream = client.GetStream();
            var bytes = Encoding.ASCII.GetBytes(options);
            await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            var buffer = new byte[2048];
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read <= 0)
            {
                return $"Probe: {registrar} accepted a TCP connection but closed it without answering SIP — the registrar is ignoring this client (temporary lock-out after failed logins?).";
            }

            var text = Encoding.ASCII.GetString(buffer, 0, read);
            var firstLine = text.Split('\r', '\n')[0];
            return $"Probe: the registrar answers SIP over TCP (\"{firstLine}\") but not over UDP — something filters UDP 5060 on this computer or network. Set Transport to TCP on the Account tab.";
        }
        catch (OperationCanceledException)
        {
            return $"Probe: {registrar} accepted a TCP connection but did not answer a SIP OPTIONS within {timeout.TotalSeconds:0}s — the registrar is ignoring this client (temporary lock-out after failed logins?).";
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionAborted or SocketError.ConnectionReset } se)
        {
            return $"Probe: the TCP connection to {registrar} was cut ({se.SocketErrorCode}) the moment SIP data was sent. A security product on this computer " +
                   "(endpoint firewall or VPN client with application control, e.g. FortiClient) is blocking SIP. Disconnect the VPN client or ask IT to allow SIP/VoIP to the local network.";
        }
        catch (SocketException se) when (se.SocketErrorCode is SocketError.ConnectionAborted or SocketError.ConnectionReset)
        {
            return $"Probe: the TCP connection to {registrar} was cut ({se.SocketErrorCode}) the moment SIP data was sent. A security product on this computer " +
                   "(endpoint firewall or VPN client with application control, e.g. FortiClient) is blocking SIP. Disconnect the VPN client or ask IT to allow SIP/VoIP to the local network.";
        }
        catch (Exception ex)
        {
            return $"Probe: TCP SIP test failed: {ex.Message}";
        }
    }
}
