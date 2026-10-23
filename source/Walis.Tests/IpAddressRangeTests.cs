using System.Net;
using Walis.Core.Security;

namespace Walis.Tests;

public class IpAddressRangeTests
{
    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9", true)]
    [InlineData("203.0.113.9", "203.0.113.10", false)]
    [InlineData("203.0.113.0/24", "203.0.113.200", true)]
    [InlineData("203.0.113.0/24", "203.0.114.1", false)]
    [InlineData("10.0.0.0/8", "10.255.255.255", true)]
    [InlineData("10.0.0.0/8", "11.0.0.0", false)]
    [InlineData("198.51.100.128/25", "198.51.100.127", false)]
    [InlineData("198.51.100.128/25", "198.51.100.129", true)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    [InlineData("127.0.0.1/32", "127.0.0.1", true)]
    [InlineData("127.0.0.1/32", "127.0.0.2", false)]
    public void MatchesIpv4Ranges(string range, string address, bool expected)
    {
        Assert.True(IpAddressRange.TryParse(range, out var parsed));
        Assert.Equal(expected, parsed!.Contains(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("::1", "::1", true)]
    [InlineData("::1/128", "::2", false)]
    [InlineData("2001:db8::/32", "2001:db8:ffff::1", true)]
    [InlineData("2001:db8::/32", "2001:db9::1", false)]
    [InlineData("2001:db8:1::/48", "2001:db8:1:ffff::1", true)]
    [InlineData("2001:db8:1::/48", "2001:db8:2::1", false)]
    [InlineData("::/0", "fe80::1", true)]
    public void MatchesIpv6Ranges(string range, string address, bool expected)
    {
        Assert.True(IpAddressRange.TryParse(range, out var parsed));
        Assert.Equal(expected, parsed!.Contains(IPAddress.Parse(address)));
    }

    [Fact]
    public void Ipv4MappedIpv6PeersMatchIpv4Entries()
    {
        Assert.True(IpAddressRange.TryParse("203.0.113.0/24", out var range));

        // Kestrel on a dual-stack socket reports IPv4 clients as ::ffff:a.b.c.d.
        Assert.True(range!.Contains(IPAddress.Parse("::ffff:203.0.113.7")));
        Assert.False(range.Contains(IPAddress.Parse("::ffff:203.0.114.7")));

        // And a list written with the mapped notation still means the IPv4 network.
        Assert.True(IpAddressRange.TryParse("::ffff:10.0.0.0/104", out var mapped));
        Assert.Equal("10.0.0.0/8", mapped!.Text);
        Assert.True(mapped.Contains(IPAddress.Parse("10.9.9.9")));
    }

    [Fact]
    public void FamiliesNeverCrossMatch()
    {
        Assert.True(IpAddressRange.TryParse("::/0", out var allIpv6));
        Assert.False(allIpv6!.Contains(IPAddress.Parse("8.8.8.8")));

        Assert.True(IpAddressRange.TryParse("0.0.0.0/0", out var allIpv4));
        Assert.False(allIpv4!.Contains(IPAddress.Parse("2001:db8::1")));
        Assert.False(allIpv4.Contains(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("example.com")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/")]
    [InlineData("10.0.0.0/8/8")]
    [InlineData("10.0.0.0/ 8")]
    [InlineData("::1/129")]
    [InlineData("fe80::1%en0")]
    [InlineData("[::1]")]
    [InlineData("203.0.113.9:443")]
    [InlineData("10.1")]
    [InlineData("1")]
    [InlineData("::ffff:10.0.0.0/64")]
    public void RejectsMalformedEntries(string value)
    {
        Assert.False(IpAddressRange.TryParse(value, out var range));
        Assert.Null(range);
    }

    [Fact]
    public void NormalizesHostBitsInText()
    {
        Assert.True(IpAddressRange.TryParse("203.0.113.77/24", out var range));
        Assert.Equal("203.0.113.0/24", range!.Text);

        Assert.True(IpAddressRange.TryParse("2001:db8:abcd:1234::1/48", out var ipv6));
        Assert.Equal("2001:db8:abcd::/48", ipv6!.Text);
    }

    [Fact]
    public void ParsesListsSeparatedByLinesCommasOrSemicolons()
    {
        Assert.True(IpAddressRange.TryParseList("127.0.0.1/32\n::1/128, 203.0.113.0/24; 2001:db8::/32\r\n",
            out var ranges, out var invalid));

        Assert.Null(invalid);
        Assert.Equal(4, ranges.Count);
        Assert.True(IpAddressRange.AnyContains(ranges, IPAddress.Parse("203.0.113.5")));
        Assert.True(IpAddressRange.AnyContains(ranges, IPAddress.IPv6Loopback));
        Assert.False(IpAddressRange.AnyContains(ranges, IPAddress.Parse("198.51.100.1")));

        Assert.True(IpAddressRange.TryParseList(null, out var empty, out _));
        Assert.Empty(empty);
    }

    [Fact]
    public void ListReportsTheOffendingEntryAndTheSizeLimit()
    {
        Assert.False(IpAddressRange.TryParseList("127.0.0.1\nnot-an-ip\n::1", out var ranges, out var invalid));
        Assert.Equal("not-an-ip", invalid);
        Assert.Empty(ranges);

        var tooMany = string.Join('\n', Enumerable.Range(0, IpAddressRange.MaximumEntries + 1)
            .Select(index => $"10.0.{index}.0/24"));
        Assert.False(IpAddressRange.TryParseList(tooMany, out _, out var overflow));
        Assert.Equal($"10.0.{IpAddressRange.MaximumEntries}.0/24", overflow);
    }
}
