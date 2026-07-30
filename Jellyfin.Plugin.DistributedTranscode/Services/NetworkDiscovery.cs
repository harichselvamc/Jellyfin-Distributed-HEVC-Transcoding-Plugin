using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Jellyfin.Plugin.DistributedTranscode.Services;

internal static class NetworkDiscovery
{
    public static IReadOnlyList<IPAddress> GetLocalIpv4Addresses()
    {
        var addresses = new List<IPAddress>();

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            var properties = networkInterface.GetIPProperties();
            foreach (var unicastAddress in properties.UnicastAddresses)
            {
                if (unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(unicastAddress.Address))
                {
                    addresses.Add(unicastAddress.Address);
                }
            }
        }

        return addresses;
    }
}
