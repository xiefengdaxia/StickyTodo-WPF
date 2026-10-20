namespace StickyTodo.Core.Entitlements;

/// <summary>
/// 发行版使用：所有用户都是永久会员，全部功能无限制。
/// 注意：这不是把界面里的判断删掉，而是让判断永远通过，界面状态依然完整。
/// </summary>
public sealed class PermanentFreeEntitlement : IEntitlementService
{
    public PlanTier Tier => PlanTier.Lifetime;

    public string DisplayName => "永久会员";

    public bool IsPro => true;

    public int MaxBoards => int.MaxValue;

    public int MaxTags => int.MaxValue;

    public int MaxRepeatRules => int.MaxValue;

    public bool CanUseIcsCalendar => true;

    public bool CanExportExcel => true;
}