using System.ComponentModel.DataAnnotations;
using PersonalOrderDesk.Domain;

namespace PersonalOrderDesk.Pages.WorkItems;

public sealed class WorkItemFormInput
{
    public long? ExpectedVersion { get; set; }
    public bool IsHistoricalImport { get; set; }
    [Display(Name = "业务订单编号")]
    [StringLength(120)]
    public string? BusinessOrderNo { get; set; }
    [Required, StringLength(200)]
    [Display(Name = "项目名称")]
    public string ProjectName { get; set; } = "";
    [Display(Name = "项目类型")]
    public ProjectType ProjectType { get; set; }
    [Display(Name = "工期类型覆盖")]
    public ScheduleCategory? ScheduleCategory { get; set; }
    [Display(Name = "物流需求覆盖")]
    public ShippingRequirement? ShippingRequirement { get; set; }
    [Display(Name = "接单日期")]
    public DateOnly? ReceivedDate { get; set; }
    [Display(Name = "截止日期模式")]
    public DueDateMode DueDateMode { get; set; }
    [Display(Name = "指定截止日期")]
    public DateOnly? SpecifiedDueDate { get; set; }
    [Display(Name = "当前工作")]
    [StringLength(4000)]
    public string? CurrentWork { get; set; }
    [Display(Name = "下一步")]
    [StringLength(2000)]
    public string? NextStep { get; set; }
    [Display(Name = "订单金额（元）")]
    [Range(typeof(decimal), "0", "999999999", ErrorMessage = "金额不能小于 0。")]
    public decimal? OrderAmount { get; set; }
    [Display(Name = "工程目录")]
    [StringLength(1000)]
    public string? ProjectPath { get; set; }
    [Display(Name = "备注")]
    [StringLength(5000)]
    public string? Notes { get; set; }
    [Display(Name = "应用新类型默认值")]
    public bool? ApplyProjectTypeDefaults { get; set; }
    [Display(Name = "金额修改原因")]
    [StringLength(500)]
    public string? OrderAmountChangeReason { get; set; }
}
