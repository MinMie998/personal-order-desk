using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Pages.Access;

public sealed class LoginModel(NetworkAccessService network) : PageModel
{
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? ErrorMessage { get; private set; }
    public IActionResult OnGet() => NetworkAccessService.IsLocal(HttpContext) ? RedirectToPage("/Index") : Page();
    public async Task<IActionResult> OnPostAsync()
    {
        if (!network.VerifyPassword(HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", Password ?? ""))
        { ErrorMessage = "密码错误或尝试次数过多。连续5次错误后请等待15分钟。"; ModelState.Remove(nameof(Password)); Password = ""; return Page(); }
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "个人工作台"), new Claim("network-revision", network.Current.Revision!) }, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }
    public async Task<IActionResult> OnPostLogoutAsync()
    { await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return RedirectToPage(); }
}
