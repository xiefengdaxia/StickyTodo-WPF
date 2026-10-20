using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StickyTodo.Core.Calendar;

namespace StickyTodo.App.ViewModels;

/// <summary>日历里的一天。</summary>
public partial class CalendarDayViewModel : ObservableObject
{
    public CalendarDayViewModel(MonthCell cell)
    {
        Date = cell.Date;
        IsCurrentMonth = cell.IsCurrentMonth;
        IsWeekend = cell.IsWeekend;
        IsToday = cell.Date == DateOnly.FromDateTime(DateTime.Now);
    }

    public DateOnly Date { get; }

    public bool IsCurrentMonth { get; }

    public bool IsWeekend { get; }

    public bool IsToday { get; }

    public string DayText => Date.Day.ToString();

    public ObservableCollection<CalendarEntryViewModel> Entries { get; } = new();

    public bool HasEntries => Entries.Count > 0;

    public string Summary => Entries.Count == 0
        ? string.Empty
        : Entries.Count == 1
            ? Entries[0].Title
            : $"{Entries[0].Title} 等 {Entries.Count} 项";

    public void SetEntries(IEnumerable<CalendarEntryViewModel> entries)
    {
        Entries.Clear();
        foreach (var entry in entries)
        {
            Entries.Add(entry);
        }

        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(Summary));
    }
}

/// <summary>日历格子里的一条（可能是重复待办展开出来的某一天）。</summary>
public sealed class CalendarEntryViewModel
{
    public CalendarEntryViewModel(CalendarEntry entry)
    {
        Entry = entry;
    }

    public CalendarEntry Entry { get; }

    public string Title => Entry.Todo.Title;

    public string TodoId => Entry.Todo.Id;

    public bool IsDone => Entry.Todo.IsDone;

    public bool IsRecurring => Entry.IsRecurring;

    public string TimeText => Entry.Occurrence.Hour == 0 && Entry.Occurrence.Minute == 0
        ? string.Empty
        : Entry.Occurrence.ToString("HH:mm");

    public string BoardTag => Entry.IsVirtual ? "重复" : string.Empty;
}