namespace StickyTodo.Core.Entitlements;

/// <summary>
/// 免费版自由额度（2 看板 / 3 标签 / 5 条重复规则，不含 ICS 与导出）。
/// 目前不启用，保留它是为了：① 单元测试可以验证闸门真的有效；
/// ② 将来要收费时，把 DI 注册从 PermanentFreeEntitlement 换成它即可。
/// </summary>
public sealed class FreeTierEntitlement : IEntitlementService
{
    public PlanTier Tier => PlanTier.Free;

    public string DisplayName => "免费版";

    public bool IsPro => false;

    public int MaxBoards => 2;

    public int MaxTags => 3;

    public int MaxRepeatRules => 5;

    public bool CanUseIcsCalendar => false;

    public bool CanExportExcel => true;
}