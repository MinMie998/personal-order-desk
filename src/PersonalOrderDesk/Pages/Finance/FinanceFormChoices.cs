namespace PersonalOrderDesk.Pages.Finance;
public static class FinanceFormChoices
{
    public static string? PaymentMethod(string? selection, string? custom)
        => selection == "其他" && !string.IsNullOrWhiteSpace(custom) ? custom.Trim() : selection;
}
