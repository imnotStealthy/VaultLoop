using System;
using System.Net;
using System.Net.Sockets;

namespace ReplayGlitchGTA;

/// <summary>
/// A parsed IPv4 or IPv6 network prefix.
/// </summary>
/// <remarks>
/// Windows Firewall rewrites the addresses stored on a rule: an IPv4 prefix given as
/// <c>192.81.241.0/24</c> is read back as <c>192.81.241.0/255.255.255.0</c>, and a bare
/// address is read back with a full-length mask. Comparing a rule against an expected
/// address set therefore has to happen on a canonical form rather than on raw text.
/// </remarks>
internal sealed class IpPrefix : IEquatable<IpPrefix>
{
    private readonly byte[] _network;

    private IpPrefix(AddressFamily addressFamily, byte[] network, int prefixLength)
    {
        AddressFamily = addressFamily;
        _network = network;
        PrefixLength = prefixLength;
        Canonical = $"{new IPAddress(network)}/{prefixLength}";
    }

    internal AddressFamily AddressFamily { get; }

    internal int PrefixLength { get; }

    /// <summary>The masked network address, for containment tests between prefixes.</summary>
    internal IPAddress NetworkAddress => new(_network);

    /// <summary>
    /// True when this prefix lies entirely inside <paramref name="container"/>: its network
    /// address is covered and it is no larger.
    /// </summary>
    internal bool IsInside(IpPrefix container) =>
        container.AddressFamily == AddressFamily &&
        PrefixLength >= container.PrefixLength &&
        container.Contains(NetworkAddress);

    /// <summary>
    /// The masked network address followed by the prefix length, for example
    /// <c>192.81.241.0/24</c>. Two prefixes describe the same network exactly when their
    /// canonical forms are equal.
    /// </summary>
    internal string Canonical { get; }

    /// <summary>
    /// Parses an address with an optional suffix, accepting every form Windows Firewall
    /// produces: a bare address, a prefix length, or an IPv4 dotted subnet mask.
    /// Returns <c>null</c> when the value is not a well-formed prefix.
    /// </summary>
    internal static IpPrefix? TryParse(string value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var separator = trimmed.IndexOf('/');
        var addressText = separator < 0 ? trimmed : trimmed.Substring(0, separator);
        var suffix = separator < 0 ? "" : trimmed.Substring(separator + 1).Trim();
        if (!IPAddress.TryParse(addressText, out var address) ||
            (address.AddressFamily != AddressFamily.InterNetwork &&
             address.AddressFamily != AddressFamily.InterNetworkV6))
        {
            return null;
        }

        var addressBytes = address.GetAddressBytes();
        var maximumPrefixLength = addressBytes.Length * 8;
        int prefixLength;
        if (suffix.Length == 0)
        {
            prefixLength = maximumPrefixLength;
        }
        else if (int.TryParse(suffix, out var declaredLength))
        {
            prefixLength = declaredLength;
        }
        else if (IPAddress.TryParse(suffix, out var mask) &&
                 mask.AddressFamily == AddressFamily.InterNetwork &&
                 address.AddressFamily == AddressFamily.InterNetwork &&
                 TryGetPrefixLength(mask.GetAddressBytes(), out var maskLength))
        {
            prefixLength = maskLength;
        }
        else
        {
            return null;
        }

        if (prefixLength < 0 || prefixLength > maximumPrefixLength)
        {
            return null;
        }

        return new IpPrefix(address.AddressFamily, Mask(addressBytes, prefixLength), prefixLength);
    }

    internal bool Contains(IPAddress address)
    {
        if (address is null || address.AddressFamily != AddressFamily)
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        if (addressBytes.Length != _network.Length)
        {
            return false;
        }

        var wholeBytes = PrefixLength / 8;
        for (var index = 0; index < wholeBytes; index++)
        {
            if (addressBytes[index] != _network[index])
            {
                return false;
            }
        }

        var remainingBits = PrefixLength % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        var partialMask = (byte)(0xFF << (8 - remainingBits));
        return (addressBytes[wholeBytes] & partialMask) == (_network[wholeBytes] & partialMask);
    }

    public bool Equals(IpPrefix? other) =>
        other is not null &&
        string.Equals(Canonical, other.Canonical, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as IpPrefix);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Canonical);

    public override string ToString() => Canonical;

    private static byte[] Mask(byte[] addressBytes, int prefixLength)
    {
        var masked = new byte[addressBytes.Length];
        for (var index = 0; index < addressBytes.Length; index++)
        {
            var bitOffset = index * 8;
            if (prefixLength >= bitOffset + 8)
            {
                masked[index] = addressBytes[index];
            }
            else if (prefixLength > bitOffset)
            {
                masked[index] =
                    (byte)(addressBytes[index] & (0xFF << (8 - (prefixLength - bitOffset))));
            }
        }
        return masked;
    }

    private static bool TryGetPrefixLength(byte[] maskBytes, out int prefixLength)
    {
        prefixLength = 0;
        var seenZeroBit = false;
        foreach (var maskByte in maskBytes)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                if ((maskByte & (1 << bit)) != 0)
                {
                    if (seenZeroBit)
                    {
                        return false;
                    }
                    prefixLength++;
                }
                else
                {
                    seenZeroBit = true;
                }
            }
        }
        return true;
    }
}
