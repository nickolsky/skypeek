using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Skypeek.Core.Models;

/// <summary>An IPv4 or IPv6 network (an address with a prefix length; a bare address is a /32 or /128).</summary>
public readonly record struct IpNet(IPAddress Network, int PrefixLength)
{
    public AddressFamily Family => Network.AddressFamily;
    public int MaxPrefix => Family == AddressFamily.InterNetwork ? 32 : 128;
    public bool IsSingleAddress => PrefixLength == MaxPrefix;
    public bool IsWorld => PrefixLength == 0;

    /// <summary>Parses "10.0.0.0/16", "10.1.2.3" or an IPv6 form. The address is masked to the network.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out IpNet? net)
    {
        net = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Trim().Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address)
            || address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            return false;
        // "10.1" parses as an address on some platforms; require the full dotted form.
        if (address.AddressFamily == AddressFamily.InterNetwork && parts[0].Count(c => c == '.') != 3)
            return false;
        var max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var length = max;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out length) || length < 0 || length > max))
            return false;
        net = new IpNet(Mask(address, length), length);
        return true;
    }

    public static IpNet? Parse(string? text) => TryParse(text, out var net) ? net : null;

    /// <summary>Every address of <paramref name="other"/> is inside this network.</summary>
    public bool Contains(IpNet other) =>
        other.Family == Family && other.PrefixLength >= PrefixLength && SamePrefix(Network, other.Network, PrefixLength);

    public bool Contains(IPAddress address) =>
        address.AddressFamily == Family && SamePrefix(Network, address, PrefixLength);

    /// <summary>The two networks share at least one address.</summary>
    public bool Overlaps(IpNet other) =>
        other.Family == Family && SamePrefix(Network, other.Network, Math.Min(PrefixLength, other.PrefixLength));

    public override string ToString() => $"{Network}/{PrefixLength}";

    private static bool SamePrefix(IPAddress a, IPAddress b, int bits)
    {
        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();
        for (var bit = 0; bit < bits; bit++)
        {
            var mask = (byte)(0x80 >> (bit % 8));
            if ((x[bit / 8] & mask) != (y[bit / 8] & mask))
                return false;
        }
        return true;
    }

    private static IPAddress Mask(IPAddress address, int bits)
    {
        var bytes = address.GetAddressBytes();
        for (var bit = bits; bit < bytes.Length * 8; bit++)
            bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        return new IPAddress(bytes);
    }
}
