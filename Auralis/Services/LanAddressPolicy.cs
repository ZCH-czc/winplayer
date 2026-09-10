using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Auralis.Services;

internal static class LanAddressPolicy
{
    internal static bool IsAllowedPeer(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Unique-local (fc00::/7) and link-local (fe80::/10) IPv6 ranges.
            return (bytes[0] & 0xfe) == 0xfc ||
                   (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80);
        }

        return false;
    }

    internal static IReadOnlyList<IPAddress> GetAdvertisableAddresses()
    {
        var addresses = new List<(IPAddress Address, bool HasDefaultGateway, int InterfaceRank, long Speed)>();
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var properties = network.GetIPProperties();
                var hasDefaultGateway = properties.GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(gateway.Address) &&
                    !gateway.Address.Equals(IPAddress.Any));
                var interfaceRank = network.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => 0,
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT => 1,
                    _ => 2
                };
                foreach (var unicast in properties.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork &&
                        IsAllowedPeer(address) && !IPAddress.IsLoopback(address))
                    {
                        addresses.Add((address, hasDefaultGateway, interfaceRank, Math.Max(0, network.Speed)));
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // An unavailable adapter list only removes advertised LAN URLs; loopback still works.
        }

        // Copy/open actions use the first address. Prefer the adapter that actually owns a
        // default route so Hyper-V/WSL/Docker host-only addresses do not become the shared link.
        return addresses
            .GroupBy(candidate => candidate.Address)
            .Select(group => group
                .OrderByDescending(candidate => candidate.HasDefaultGateway)
                .ThenBy(candidate => candidate.InterfaceRank)
                .ThenByDescending(candidate => candidate.Speed)
                .First())
            .OrderByDescending(candidate => candidate.HasDefaultGateway)
            .ThenBy(candidate => candidate.InterfaceRank)
            .ThenByDescending(candidate => candidate.Speed)
            .ThenBy(candidate => candidate.Address.ToString(), StringComparer.Ordinal)
            .Select(candidate => candidate.Address)
            .ToArray();
    }
}
