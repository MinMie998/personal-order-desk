using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Pages.Shipping;

public sealed class IndexModel(IShippingService shipping) : PageModel
{
    [BindProperty(SupportsGet = true)] public string View { get; set; } = "All";
    public IReadOnlyList<ShippingQueueItemDto> Items { get; private set; } = [];
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (View == "ToShip" || View == "Overdue")
        {
            var eligible = await shipping.GetQueue(ShippingQueueFilter.ToShip, cancellationToken);
            Items = View == "Overdue" ? eligible.Where(x => x.ShippingUrgency == "寄出已逾期").ToArray() : eligible;
        }
        else if (View == "NeedsConfirmation") Items = await shipping.GetQueue(ShippingQueueFilter.NeedsConfirmation, cancellationToken);
        else if (View == "History") Items = await shipping.GetQueue(ShippingQueueFilter.History, cancellationToken);
        else
        {
            var all = await shipping.GetQueue(ShippingQueueFilter.All, cancellationToken);
            Items = View == "Shipped" ? all.Where(x => x.ShipmentStatus is Domain.ShipmentStatus.Shipped or Domain.ShipmentStatus.Delivered).ToArray() : all;
            View = View is "Shipped" ? View : "All";
        }
    }
}
