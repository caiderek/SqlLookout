using SqlLookout.Core;

namespace SqlLookout.Tests;

public class ServerInfoTests
{
    [Theory]
    [InlineData("10.0.1600.22", 10, 0, 1600)]     // 2008 RTM
    [InlineData("10.50.6000.34", 10, 50, 6000)]   // 2008 R2 SP3
    [InlineData("11.0.7507.2", 11, 0, 7507)]      // 2012
    [InlineData("17.0.1000.7", 17, 0, 1000)]      // 2025
    public void Parse_reads_major_minor_build(string version, int major, int minor, int build)
    {
        var info = ServerInfo.Parse(version, 2);
        Assert.Equal((major, minor, build), (info.Major, info.Minor, info.Build));
    }

    [Theory]
    [InlineData("")]
    [InlineData("10")]
    [InlineData("ten.zero.1")]
    public void Parse_rejects_garbage(string version) =>
        Assert.Throws<FormatException>(() => ServerInfo.Parse(version, 2));

    [Theory]
    [InlineData(10, 0, 1600, false)]     // 2008 RTM: no dm_os_volume_stats
    [InlineData(10, 0, 6000, false)]     // 2008 SP4 is still major.minor 10.0
    [InlineData(10, 50, 1600, false)]    // 2008 R2 RTM
    [InlineData(10, 50, 2500, true)]     // 2008 R2 SP1
    [InlineData(11, 0, 0, true)]
    [InlineData(17, 0, 0, true)]
    public void DiskSpace_support_follows_2008R2_SP1(int major, int minor, int build, bool supported)
    {
        var reason = new Core.Collectors.DiskSpaceCollector().UnsupportedReason(new ServerInfo(major, minor, build, 2));
        Assert.Equal(supported, reason is null);
    }

    [Fact]
    public void Express_is_engine_edition_4()
    {
        Assert.True(new ServerInfo(16, 0, 1000, 4).IsExpress);
        Assert.False(new ServerInfo(16, 0, 1000, 2).IsExpress);
    }
}
