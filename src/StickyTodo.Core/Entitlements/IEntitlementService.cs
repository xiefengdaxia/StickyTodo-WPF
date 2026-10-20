namespace StickyTodo.Core.Entitlements;

/// <summary>
/// 权益判断的唯一入口。
/// 业务代码只依赖这个接口，不要直接读常量、也不要在界面里写 if (IsVip)。
/// 换档位 = 换实现类，业务与界面代码零改动。
/// </summary>
public interface IEntitlementService
{
    PlanTier Tier { get; }

    string DisplayName { get; }

    bool IsPro { get; }

    int MaxBoards { get; }

    int MaxTags { get; }

    int MaxRepeatRules { get; }

    bool CanUseIcsCalendar { get; }

    bool CanExportExcel { get; }
}