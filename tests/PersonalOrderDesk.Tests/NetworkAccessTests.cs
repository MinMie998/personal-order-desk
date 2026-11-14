using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Tests;
public sealed class NetworkAccessTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PersonalOrderDesk-network-" + Guid.NewGuid().ToString("N"));
    private const string TestPassword = "Synthetic-Login-2026!";
    private NetworkAccessService Enabled()
    {
        new NetworkAccessService(root, ["192.168.1.8"]).Save(true, TestPassword, TestPassword);
        return new(root, ["192.168.1.8", "8.8.8.8"]);
    }

    [Fact]
    public void DisabledByDefaultAndCannotEnableWithoutPassword()
    {
        var service = new NetworkAccessService(root, []);
        Assert.False(service.Current.Enabled);
        Assert.Throws<ArgumentException>(() => service.Save(true, null, null));
        Assert.Throws<ArgumentException>(() => service.Save(true, "short", "short"));
        Assert.Throws<ArgumentException>(() => service.Save(true, TestPassword, "different"));
    }

    [Fact]
    public void StoredPasswordIsSaltedAndChangesInvalidatePreviousSessionRevision()
    {
        var service = Enabled();
        Assert.DoesNotContain(TestPassword, File.ReadAllText(Path.Combine(root, "network-access.json")));
        Assert.True(service.VerifyPassword("192.168.1.2", TestPassword));
        Assert.Equal(new[] { "192.168.1.8" }, service.Addresses);
        service.Save(true, "Changed-Password-2026!", "Changed-Password-2026!");
        var updated = new NetworkAccessService(root, []);
        Assert.NotEqual(service.Current.Revision, updated.Current.Revision);
        Assert.False(updated.VerifyPassword("192.168.1.2", TestPassword));
        Assert.True(updated.VerifyPassword("192.168.1.2", "Changed-Password-2026!"));
    }

    [Fact]
    public void FiveFailedLoginsThrottleThatAddressWithoutBlockingAnotherAddress()
    {
        var service = Enabled();
        for (var i = 0; i < 5; i++) Assert.False(service.VerifyPassword("192.168.1.2", "incorrect"));
        Assert.False(service.VerifyPassword("192.168.1.2", TestPassword));
        Assert.True(service.VerifyPassword("192.168.1.3", TestPassword));
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.1.8", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    public void OnlyPrivateIpv4AddressesAreAdvertised(string address, bool expected)
        => Assert.Equal(expected, NetworkAccessService.IsPrivateAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("/WorkItems", false, false, 302)]
    [InlineData("/WorkItems", true, false, 200)]
    [InlineData("/Finance", true, true, 302)]
    [InlineData("/healthz", false, false, 302)]
    [InlineData("/Settings", true, false, 403)]
    [InlineData("/Settings/Network", true, false, 403)]
    [InlineData("/Settings/DataTransfer", true, false, 403)]
    [InlineData("/Access/Login", false, false, 200)]
    public async Task RemoteRequestsRequireCurrentLoginAndCannotUseLocalAdministration(string path, bool login, bool stale, int expected)
    {
        var network = Enabled(); var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.2"); context.Request.Path = path;
        if (login) context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("network-revision", stale ? "old" : network.Current.Revision!)], "test"));
        var reached = false;
        await new LanAccessMiddleware(_ => { reached = true; return Task.CompletedTask; }).InvokeAsync(context, network);
        Assert.Equal(expected, context.Response.StatusCode); Assert.Equal(expected == 200, reached);
    }

    [Fact]
    public async Task DisabledNetworkAndPublicRemoteAddressesAreRejectedWhileLoopbackRemainsUsable()
    {
        var disabled = new NetworkAccessService(root, []);
        foreach (var (remote, expected) in new[] { ("192.168.1.2", 403), ("8.8.8.8", 403), ("127.0.0.1", 200) })
        {
            var context = new DefaultHttpContext(); context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
            await new LanAccessMiddleware(_ => Task.CompletedTask).InvokeAsync(context, disabled);
            Assert.Equal(expected, context.Response.StatusCode);
        }
    }

    [Fact]
    public async Task LanHostAndOriginMustMatchRegisteredInterfaceAndPort()
    {
        var network = Enabled();
        foreach (var (host, origin, expected) in new[] { ("192.168.1.8:5059", "http://192.168.1.8:5059", 200),
            ("192.168.1.9:5059", "http://192.168.1.9:5059", 400), ("192.168.1.8:5059", "https://evil.test", 403),
            ("192.168.1.8:5058", "http://192.168.1.8:5058", 400) })
        {
            var context = new DefaultHttpContext(); context.Request.Host = new HostString(host); context.Request.Headers.Origin = origin;
            await new LocalAccessMiddleware(_ => Task.CompletedTask, network).InvokeAsync(context);
            Assert.Equal(expected, context.Response.StatusCode);
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
