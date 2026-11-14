namespace PersonalOrderDesk.Services;

public sealed class LanAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, NetworkAccessService network)
    {
        if (NetworkAccessService.IsLocal(context)) { await next(context); return; }
        if (!network.Current.Enabled || context.Connection.RemoteIpAddress is not { } remote || !NetworkAccessService.IsPrivateAddress(remote))
        { context.Response.StatusCode = 403; return; }
        if (context.Request.Path.StartsWithSegments("/api/v1")) { await next(context); return; }
        if (context.Request.Path.StartsWithSegments("/Settings"))
        { context.Response.StatusCode = 403; await context.Response.WriteAsync("设置、备份恢复和退出应用只允许在电脑本机操作。"); return; }
        if (context.Request.Path.StartsWithSegments("/Access/Login") || context.Request.Path.StartsWithSegments("/css")
            || context.Request.Path.StartsWithSegments("/js") || context.Request.Path.StartsWithSegments("/lib")
            || context.Request.Path.StartsWithSegments("/icons") || context.Request.Path == "/favicon.ico"
            || context.Request.Path == "/PersonalOrderDesk.styles.css")
        { await next(context); return; }
        if (context.User.Identity?.IsAuthenticated != true || context.User.FindFirst("network-revision")?.Value != network.Current.Revision)
        {
            context.Response.Redirect("/Access/Login?returnUrl=" + Uri.EscapeDataString(context.Request.PathBase + context.Request.Path + context.Request.QueryString));
            return;
        }
        await next(context);
    }
}
