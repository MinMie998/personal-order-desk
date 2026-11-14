using Microsoft.AspNetCore.Http;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Tests;

public sealed class LocalAccessTests
{
    [Theory]
    [InlineData("127.0.0.1:5059", null, 200)]
    [InlineData("localhost:5059", "http://localhost:5059", 200)]
    [InlineData("127.0.0.1:5059", "http://127.0.0.1:5059", 200)]
    [InlineData("workbench.attacker.test:5059", null, 400)]
    [InlineData("127.0.0.1", null, 400)]
    [InlineData("127.0.0.1:5059", "https://example.com", 403)]
    [InlineData("127.0.0.1:5059", "http://127.0.0.1:5058", 403)]
    [InlineData("127.0.0.1:5059", "null", 403)]
    [InlineData("127.0.0.1:5059", "https://127.0.0.1:5059", 403)]
    [InlineData("127.0.0.1:5059", "", 403)]
    public async Task Only_explicit_loopback_hosts_and_origins_reach_application_handlers(string host, string? origin, int status)
    {
        var called = false;
        var middleware = new LocalAccessMiddleware(_ => { called = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        if (origin is not null) context.Request.Headers["Origin"] = origin;
        await middleware.InvokeAsync(context);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(status == 200, called);
    }
}
