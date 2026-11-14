using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FreedomInstaller.Core;

/// <summary>
/// Verifies that a USB connection really uses the expected adapter/subnet.
/// SSH.NET's pinned client cannot bind a source address for its TCP connect, so
/// this guard rejects any bind address that is not assigned to a live local
/// interface on the same /24 as the fixed device address. The per-handshake
/// check in addition requires the operating system's selected IPv4 source for
/// the numeric destination to be exactly the configured bind address. It does
/// not claim OS-level source binding for the SSH socket.
/// </summary>
internal static class OfflineUpdateSshUsbRoute
{
    internal static void Verify(string host, string? bindAddress)
    {
        if (bindAddress is null)
            return;

        var (hostIp, _) = ParseUsbAddresses(host, bindAddress);
        var hostBytes = hostIp.GetAddressBytes();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                        !string.Equals(unicast.Address.ToString(), bindAddress, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (unicast.PrefixLength != 24)
                    {
                        throw new IOException(
                            "The USB adapter address does not use the required /24 prefix; refusing an unverified route.");
                    }

                    var addressBytes = unicast.Address.GetAddressBytes();
                    for (var i = 0; i < 3; i++)
                    {
                        if (addressBytes[i] != hostBytes[i])
                        {
                            throw new IOException(
                                "The USB adapter address is on a different subnet than the tablet; refusing an unverified route.");
                        }
                    }

                    return;
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            throw new IOException("The USB adapter addresses could not be inspected; refusing an unverified route.", ex);
        }

        throw new IOException(
            "The configured USB bind address is not assigned to a local adapter on the device subnet; refusing an unverified route.");
    }

    /// <summary>
    /// Requires the source address the operating system would select for the
    /// numeric USB destination to be exactly the configured bind address. This
    /// runs immediately before each managed handshake, because the route can
    /// change between the key preflight and the password handshake. An
    /// unresolved or different source is rejected; there is no fallback. A
    /// null bind address (Wi-Fi) is left untouched.
    /// </summary>
    internal static void VerifySelectedSource(
        string host,
        string? bindAddress,
        Func<IPAddress, IPAddress?> resolveSelectedSource)
    {
        ArgumentNullException.ThrowIfNull(resolveSelectedSource);
        if (bindAddress is null)
            return;

        var (hostIp, localIp) = ParseUsbAddresses(host, bindAddress);
        var selected = resolveSelectedSource(hostIp);
        if (selected is null ||
            selected.AddressFamily != AddressFamily.InterNetwork ||
            !selected.Equals(localIp))
        {
            throw new IOException(
                "The operating system did not select the configured USB bind address as the source for the tablet; refusing an unverified route.");
        }
    }

    /// <summary>
    /// Asks the operating system which IPv4 source it would use for the
    /// destination. A connected UDP socket performs no packet exchange here;
    /// reading LocalEndPoint only exposes the route selection.
    /// </summary>
    internal static IPAddress? ResolveSelectedSource(IPAddress destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.AddressFamily != AddressFamily.InterNetwork)
            return null;

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(destination, 9));
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static (IPAddress Host, IPAddress Local) ParseUsbAddresses(string host, string bindAddress)
    {
        if (!IPAddress.TryParse(host, out var hostIp) || hostIp.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.TryParse(bindAddress, out var localIp) || localIp.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new IOException("The USB route guard requires an IPv4 device address and bind address.");
        }

        var hostBytes = hostIp.GetAddressBytes();
        var localBytes = localIp.GetAddressBytes();
        if (hostBytes[0] != 172 || hostBytes[1] != 16 || hostBytes[2] != 42 ||
            localBytes[0] != 172 || localBytes[1] != 16 || localBytes[2] != 42)
        {
            throw new IOException("The USB route guard only accepts the offline-update USB subnet 172.16.42.0/24.");
        }

        return (hostIp, localIp);
    }
}
