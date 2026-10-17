using InternetMonitor.Core.Tools.Mtu;
using Xunit;

namespace InternetMonitor.Tests.Core.Tools;

/// <summary>The MTU search over a pretend path that fragments above a known size.</summary>
public sealed class PathMtuProbeTests
{
    [Theory]
    [InlineData(1472, 1500)]
    [InlineData(1464, 1492)]
    [InlineData(1372, 1400)]
    [InlineData(32, 60)]
    public async Task The_largest_fitting_payload_plus_the_headers_is_the_path_mtu(int largestPayload, int expectedMtu)
    {
        int probes = 0;
        Task<bool> Fits(int size)
        {
            probes++;
            return Task.FromResult(size <= largestPayload);
        }

        int found = await PathMtuProbe.FindLargestAsync(PathMtuProbe.SmallestPayload, PathMtuProbe.LargestPayload, Fits);

        Assert.Equal(largestPayload, found);
        Assert.Equal(expectedMtu, found + PathMtuProbe.HeaderBytes);
        Assert.InRange(probes, 1, 12);
    }
}
