using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using AuraMouse.Transfers;

namespace AuraMouse;

/// What a computer tells the others so they can turn it on later. A computer may have a cable and
/// Wi-Fi, and we can't know which one it will be on when it's off, so it sends them all.
internal sealed record WakeInfo(string Name, List<string> Macs);

/// A saved computer that can be turned on from here.
internal sealed record WakeTarget(string Name, List<string> Macs, string? Ip);

/// Wake-on-LAN: a broadcast the network card listens for while the computer is off. It only travels
/// inside the local network.
internal static partial class Wake
{
    const int MaxMacs = 8, Repeats = 3;
    static readonly int[] Ports = [9, 7]; // the two ports people use for this; cards don't actually check
    static readonly string[] NotReal = ["virtual", "hyper-v", "vethernet", "vmware", "wsl", "bluetooth", "vpn", "tap-", "tailscale", "miniport"];

    [GeneratedRegex("^[0-9A-F]{12}$")]
    private static partial Regex MacPattern();

    /// The real network cards of this computer, connected or not.
    public static List<string> LocalMacs()
    {
        var macs = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet))
                continue;
            var label = (nic.Name + " " + nic.Description).ToLowerInvariant();
            if (NotReal.Any(label.Contains)) continue;
            macs.Add(nic.GetPhysicalAddress().ToString());
        }
        return Clean(macs);
    }

    /// These come from the network too, so only well-formed addresses of real cards get through.
    public static List<string> Clean(IEnumerable<string> macs) =>
        macs.Select(m => m.ToUpperInvariant()).Where(m => MacPattern().IsMatch(m) && m != "000000000000" && m != "FFFFFFFFFFFF")
            .Distinct().Take(MaxMacs).ToList();

    public static Msg InfoMessage() => new(MsgType.WakeInfo)
    {
        Payload = TransferJson.Bytes(new WakeInfo(Environment.MachineName, LocalMacs())),
    };

    /// A phone only connects to the host, so the host also tells it about the other computers it knows.
    public static List<Msg> PhoneMessages()
    {
        var messages = PairStore.WakeTargets
            .Where(t => !string.Equals(t.Name, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            .Select(t => new Msg(MsgType.WakeInfo) { Payload = TransferJson.Bytes(new WakeInfo(t.Name, t.Macs)) })
            .ToList();
        messages.Add(InfoMessage());
        return messages;
    }

    /// Saves what the other side told us. The name is given when we already know it for sure.
    public static void Accept(byte[]? payload, string? knownName, IPAddress? ip)
    {
        try
        {
            var info = TransferJson.Read<WakeInfo>(payload);
            var macs = Clean(info.Macs ?? []);
            var name = (knownName ?? info.Name ?? "").Trim();
            if (macs.Count > 0 && name.Length is > 0 and <= 40) PairStore.SaveWake(new WakeTarget(name, macs, ip?.ToString()));
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException) { }
    }

    /// 6 bytes of FF, then the address 16 times.
    public static byte[] MagicPacket(string mac)
    {
        var address = Convert.FromHexString(mac);
        var packet = new byte[6 + 16 * address.Length];
        packet.AsSpan(0, 6).Fill(0xFF);
        for (int i = 0; i < 16; i++) address.CopyTo(packet, 6 + i * address.Length);
        return packet;
    }

    /// Sends the signal on every network this computer is on. Returns how many it went out on.
    public static async Task<int> SendAsync(IEnumerable<string> macs)
    {
        var packets = Clean(macs).Select(MagicPacket).ToList();
        var networks = LanScan.LocalNetworks();
        int sent = 0;
        foreach (var network in networks)
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(network.Address, 0)) { EnableBroadcast = true };
                var targets = new[] { LanScan.Broadcast(network.Address, network.Mask), IPAddress.Broadcast };
                for (int round = 0; round < Repeats; round++)
                {
                    foreach (var packet in packets)
                        foreach (var target in targets)
                            foreach (var port in Ports)
                                await udp.SendAsync(packet, new IPEndPoint(target, port));
                    await Task.Delay(100);
                }
                sent++;
            }
            catch (SocketException) { } // this network just went away
        }
        return sent;
    }

    /// Whether it answers. "No" can also mean a firewall ignoring us, so it isn't proof that it's off.
    public static async Task<bool> IsUpAsync(string? ip)
    {
        if (!IPAddress.TryParse(ip, out var address)) return false;
        var ping = Task.Run(async () =>
        {
            try
            {
                using var p = new Ping();
                return (await p.SendPingAsync(address, 800)).Status == IPStatus.Success;
            }
            catch (PingException) { return false; }
        });
        var port = Task.Run(async () =>
        {
            try
            {
                using var tcp = new TcpClient();
                using var timeout = new CancellationTokenSource(800);
                await tcp.ConnectAsync(address, Net.TcpPort, timeout.Token);
                return true;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException) { return false; }
        });
        var results = await Task.WhenAll(ping, port);
        return results.Any(up => up);
    }
}
