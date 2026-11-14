namespace PersonalOrderDesk.Client;
/// <summary>Connects the native back dispatcher to the currently displayed workbench.</summary>
public sealed class ClientBackNavigation
{
    public Func<Task<bool>>? Handler { get; set; }
    public Task<bool> GoBack()=>Handler?.Invoke()??Task.FromResult(false);
}
