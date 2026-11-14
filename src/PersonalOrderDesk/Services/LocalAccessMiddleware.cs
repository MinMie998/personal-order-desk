namespace PersonalOrderDesk.Services;

/// <summary>Only the explicitly supported loopback authorities and origins may access the local desk.</summary>
public sealed class LocalAccessMiddleware(RequestDelegate next, NetworkAccessService? network = null)
{
    private static readonly HashSet<string> Authorities = new(StringComparer.OrdinalIgnoreCase)
        { "127.0.0.1:5059", "localhost:5059" };
    private static readonly HashSet<string> Origins = new(StringComparer.OrdinalIgnoreCase)
        { "http://127.0.0.1:5059", "http://localhost:5059" };

    public async Task InvokeAsync(HttpContext context)
    {
        var authority = context.Request.Host.Value ?? "";
        var lanAuthority = network is { Current.Enabled: true } && context.Request.Host.Port == 5059
            && network.Addresses.Contains(context.Request.Host.Host, StringComparer.OrdinalIgnoreCase);
        if (!Authorities.Contains(authority) && !lanAuthority)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("此地址不属于工作台允许的本机地址。");
            return;
        }
        if (context.Request.Headers.TryGetValue("Origin", out var origin)
            && !(Origins.Contains(origin.ToString()) || (lanAuthority && origin.ToString().Equals("http://" + authority, StringComparison.OrdinalIgnoreCase))))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("此请求来源不属于工作台允许的本机页面。");
            return;
        }
        await next(context);
    }
}
