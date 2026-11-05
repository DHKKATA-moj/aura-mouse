using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AuraMouse;

/// Finding the host without relying on UDP broadcast, which often never arrives: the receiving
/// computer's firewall drops it, or Windows sends it out of a virtual adapter (Hyper-V, WSL, VPN).
internal static class LanScan
{
    const int MaxConcurrent = 128;
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(700);

    internal readonly record struct LocalNetwork(IPAddress Address, IPAddress Mask, bool HasGateway);

    /// This computer's IPv4 networks, real ones (with a router) first.
    public static List<LocalNetwork> LocalNetworks()
    {
        var networks = new List<LocalNetwork>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var props = nic.GetIPProperties();
            bool hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var unicast in props.UnicastAddresses)
            {
                var ip = unicast.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                if (ip.GetAddressBytes() is [169, 254, ..]) continue; // no DHCP: not a usable network
                networks.Add(new LocalNetwork(ip, unicast.IPv4Mask, hasGateway));
            }
        }
        return networks.OrderByDescending(n => n.HasGateway).ToList();
    }

    /// Whether this address is on one of our own networks. Everything here works on the same network only,
    /// so nothing from further away has any business connecting, whatever the firewall lets through.
    public static bool IsNearby(IPAddress peer)
    {
        if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();
        if (IPAddress.IsLoopback(peer)) return true;
        if (peer.AddressFamily != AddressFamily.InterNetwork) return false;
        var theirs = peer.GetAddressBytes();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var mine = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                if (mask[0] != 0xFF) continue; // nothing real is wider than this
                if (Enumerable.Range(0, 4).All(i => (mine[i] & mask[i]) == (theirs[i] & mask[i]))) return true;
            }
        }
        return false;
    }

    /// Whether this address is this computer itself, on any of its adapters.
    public static bool IsThisComputer(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        return NetworkInterface.GetAllNetworkInterfaces().Any(nic => nic.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(ip)));
    }

    public static IPAddress Broadcast(IPAddress address, IPAddress mask)
    {
        var ip = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        return new IPAddress(ip.Select((b, i) => (byte)(b | ~m[i])).ToArray());
    }

    /// Computers on our networks (the /24 around each of our addresses) with Aura Mouse's port open.
    /// A closed port answers at once and a missing computer times out, so the whole scan takes about a second.
    public static async Task<List<IPAddress>> FindHostsAsync(CancellationToken ct)
    {
        var networks = LocalNetworks().Take(4).ToList();
        var own = networks.Select(n => n.Address).ToHashSet();
        var candidates = new HashSet<IPAddress>();
        foreach (var n in networks)
        {
            var b = n.Address.GetAddressBytes();
            for (int last = 1; last < 255; last++)
                candidates.Add(new IPAddress([b[0], b[1], b[2], (byte)last]));
        }
        candidates.ExceptWith(own);

        var found = new ConcurrentBag<IPAddress>();
        using var gate = new SemaphoreSlim(MaxConcurrent);
        await Task.WhenAll(candidates.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var tcp = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ProbeTimeout);
                await tcp.ConnectAsync(ip, Net.TcpPort, timeout.Token);
                found.Add(ip); // just checking it's there; closing without a word doesn't count as a wrong code
            }
            catch { }
            finally { gate.Release(); }
        }));
        return found.ToList();
    }

    /// The computer's name if the network knows it, otherwise its address.
    public static async Task<string> NameOfAsync(IPAddress ip)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(ip).WaitAsync(TimeSpan.FromSeconds(1.5));
            var name = entry.HostName.Split('.')[0];
            if (name.Length > 0 && !IPAddress.TryParse(name, out _)) return name.ToUpperInvariant();
        }
        catch { }
        return ip.ToString();
    }
}
