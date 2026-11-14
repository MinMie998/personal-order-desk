using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using PersonalOrderDesk.Domain;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Pages.Shipping;

public sealed class DetailsModel(IShippingService shipping, IWorkbenchService workbench, IBusinessClock clock) : PageModel
{
    public ShippingDraftDto? Draft { get; private set; }
    public WorkItemDetailDto? WorkItem { get; private set; }
    public DateOnly BusinessToday => clock.Today;
    public string? ErrorMessage { get; private set; }
    public ProjectPathCheckDto? PathCheck { get; private set; }
    [BindProperty] public ShippingDraftRequestDto DraftInput { get; set; } = new(0, null, null, null, null, false);
    [BindProperty] public MarkShippedRequestDto ShippedInput { get; set; } = new(0, default, null, null, null);
    [BindProperty] public MarkDeliveredRequestDto DeliveredInput { get; set; } = new(0, default);
    [BindProperty] public ReconcileShipmentRequestDto ReconcileInput { get; set; } = new(0, ShipmentStatus.Unknown, null, DateKnowledge.Unknown, null, DateKnowledge.Unknown, null, null, null, "");
    [BindProperty] public BackfillShipmentDatesRequestDto BackfillInput { get; set; } = new(0, null, null);

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        try { await Load(id, cancellationToken); return Page(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    public async Task<IActionResult> OnPostPrepareHistoryReviewAsync(Guid id, CancellationToken cancellationToken)
    {
        try { await shipping.PrepareHistoryReview(id, DraftInput.ExpectedVersion, cancellationToken); TempData["Notice"] = "历史物流核对草稿已建立。请按可证实资料选择状态和日期知识。"; return RedirectToPage(new { id }); }
        catch (Exception ex) when (Showable(ex)) { ErrorMessage = Explain(ex); await Load(id, cancellationToken); return Page(); }
    }

    public async Task<IActionResult> OnPostSaveDraftAsync(Guid id, CancellationToken cancellationToken)
    {
        try { await shipping.SaveDraft(id, DraftInput, cancellationToken); TempData["Notice"] = "物流草稿已保存。"; return RedirectToPage(new { id }); }
        catch (Exception ex) when (Showable(ex)) { ErrorMessage = Explain(ex); await Load(id, cancellationToken); return Page(); }
    }

    public async Task<IActionResult> OnPostMarkShippedAsync(Guid id, CancellationToken cancellationToken)
    {
        try { await shipping.MarkShipped(id, ShippedInput, cancellationToken); TempData["Notice"] = "已寄出事实已保存。"; return RedirectToPage(new { id }); }
        catch (Exception ex) when (Showable(ex)) { ErrorMessage = Explain(ex); await Load(id, cancellationToken); return Page(); }
    }

    public async Task<IActionResult> OnPostMarkDeliveredAsync(Guid id, CancellationToken cancellationToken)
    {
        try { await shipping.MarkDelivered(id, DeliveredInput, cancellationToken); TempData["Notice"] = "签收事实已保存。"; return RedirectToPage(new { id }); }
        catch (Exception ex) when (Showable(ex)) { ErrorMessage = Explain(ex); await Load(id, cancellationToken); return Page(); }
    }

    public async Task<IActionResult> OnPostReconcileAsync(Guid id, CancellationToken cancellationToken)
    {
        try { await shipping.ReconcileHistory(id, ReconcileInput, cancellationToken); TempData["Notice"] = "历史物流核对已保存。"; return RedirectToPage(new { id }); }
        catch (Exception ex) when (Showable(ex)) { ErrorMessage = Explain(ex); await Load(id, cancellationToken); return Page(); }
    }

    public async Task<IActionResult> OnPostBackfillAsync(Guid id, CancellationToken cancellationToken)
    {
        try { await shipping.BackfillDates(id, BackfillInput, cancellationToken); TempData["Notice"] = "物流日期补录已保存。"; return RedirectToPage(new { id }); }
        catch (Exception ex) when (Showable(ex)) { ErrorMessage = Explain(ex); await Load(id, cancellationToken); return Page(); }
    }

    private async Task Load(Guid id, CancellationToken ct)
    {
        WorkItem = await workbench.GetDetail(id, ct);
        if (WorkItem is null) throw new KeyNotFoundException("找不到工作项。");
        try { Draft = await shipping.GetDraft(id, ct); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("该工作项没有物流记录", StringComparison.Ordinal)) { Draft = null; }
        if (Draft is null)
        {
        DraftInput = new ShippingDraftRequestDto(WorkItem.WorkItem.Version, null, null, null, null, false);
            return;
        }
        DraftInput = new ShippingDraftRequestDto(Draft.Version, Draft.Carrier, Draft.TrackingNumber, Draft.NoTrackingNumber, Draft.LatestShipDate, Draft.FulfillmentPaused);
        ShippedInput = new MarkShippedRequestDto(Draft.Version, BusinessToday, Draft.Carrier, Draft.TrackingNumber, Draft.NoTrackingNumber);
        DeliveredInput = new MarkDeliveredRequestDto(Draft.Version, BusinessToday);
        ReconcileInput = new ReconcileShipmentRequestDto(Draft.Version, ShipmentStatus.Unknown, null, DateKnowledge.Unknown, null, DateKnowledge.Unknown, Draft.Carrier, Draft.TrackingNumber, Draft.NoTrackingNumber, "");
        BackfillInput = new BackfillShipmentDatesRequestDto(Draft.Version, Draft.ShippedDateKnowledge == DateKnowledge.Unknown ? null : Draft.ShippedDate, Draft.DeliveredDateKnowledge == DateKnowledge.Unknown ? null : Draft.DeliveredDate);
    }

    private static bool Showable(Exception ex) => ex is DbUpdateConcurrencyException or InvalidOperationException or ArgumentException or KeyNotFoundException;
    private static string Explain(Exception ex) => ex is DbUpdateConcurrencyException
        ? "工作项已在其他页面中修改。请刷新详情并核对最新状态后重试。"
        : ex.Message;

    public IActionResult OnGetCheckPath(string? path) => new JsonResult(workbench.CheckProjectPath(path));
}
