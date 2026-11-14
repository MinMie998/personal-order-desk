using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Pages.Settings;
public sealed class NetworkModel(NetworkAccessService network) : PageModel
{
    [BindProperty] public bool Enabled { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? PasswordConfirmation { get; set; }
    public NetworkAccessService Network => network;
    public string? Notice { get; private set; }
    public string? ErrorMessage { get; private set; }
    public void OnGet() => Enabled = network.Read().Enabled;
    public IActionResult OnPost()
    {
        if (!NetworkAccessService.IsLocal(HttpContext)) return StatusCode(403);
        try { network.Save(Enabled, Password, PasswordConfirmation); Notice = "已保存。请在设置页退出应用，然后从桌面重新启动，网络开关和密码才会生效。"; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { ErrorMessage = ex.Message; }
        ModelState.Remove(nameof(Password)); ModelState.Remove(nameof(PasswordConfirmation)); Password = PasswordConfirmation = null;
        return Page();
    }
}
