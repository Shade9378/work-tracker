using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ResearchTracker.Core;

namespace ResearchTracker.Desktop;

public sealed class MonthCalendarView(MainViewModel vm, Action<CalendarEvent> selectEvent, Action<WorkTask> editTask) : UserControl
{
    private static TextBlock Text(string text, string color = "TextPrimary") => new() { Text = text, FontSize = DesignTokens.Caption, Foreground = DesignTokens.Brush(color), TextTrimming = TextTrimming.CharacterEllipsis };
    public void Render()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*"), RowDefinitions = new RowDefinitions("24,*,*,*,*,*,*"), ClipToBounds = true };
        grid.MinHeight = 480;
        Content = new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var dates = CalendarDates.MonthCells(vm.CalendarDate);
        for (var i = 0; i < 7; i++)
        {
            var label = Text(dates[i].ToString("ddd").ToUpperInvariant(), "TextMuted"); label.Margin = new Thickness(8, 4); Grid.SetColumn(label, i); grid.Children.Add(label);
        }
        for (var i = 0; i < dates.Count; i++)
        {
            var day = dates[i]; var isToday = day == DateTime.Today; var past = day < DateTime.Today;
            var panel = new StackPanel { Spacing = 3, Margin = new Thickness(5), Opacity = past ? .5 : 1 };
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var number = new Button { Name = "MonthDate" + day.ToString("yyyyMMdd"), Content = day.Day == 1 ? day.ToString("d MMM") : day.Day.ToString(), Background = isToday ? DesignTokens.Brush("Accent") : Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(5, 2), HorizontalAlignment = HorizontalAlignment.Left, Foreground = DesignTokens.Brush(isToday ? "OnAccent" : past ? "PastText" : day.Month == vm.CalendarDate.Month ? "TextPrimary" : "TextMuted"), FontWeight = isToday ? FontWeight.Bold : FontWeight.Normal };
            ToolTip.SetTip(number, "Open week of " + day.ToString("D")); number.Click += (_, _) => vm.ShowCalendarDate(day, true); header.Children.Add(number);
            var add = new Button { Content = "+", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4, 1), FontSize = 11, Opacity = 0 }; Grid.SetColumn(add, 1); header.Children.Add(add);
            add.GotFocus += (_, _) => add.Opacity = 1; add.LostFocus += (_, _) => add.Opacity = 0;
            ToolTip.SetTip(add, "Schedule a new task on " + day.ToString("D")); add.Click += (_, _) => { vm.AddTask(); if (vm.Selected is WorkTask task) vm.ManualSchedule(task, day.AddHours(9)); };
            panel.Children.Add(header);
            foreach (var entry in vm.CalendarCache.Events.Where(e => e.Overlaps(day, day.AddDays(1))).Where(e => e.TaskId == null || !vm.Tasks.Any(t => t.Id == e.TaskId && t.Status != WorkStatus.Cancelled && TaskSchedule.Occurrences(t).Any(s => s.Start == e.Start && s.Start.AddHours(t.EffortHours) == e.End))).Where(e => string.IsNullOrWhiteSpace(vm.TaskQuery) || e.Title.Contains(vm.TaskQuery, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Start))
            {
                var label = new Button { Content = Text((entry.AllDay ? "" : entry.Start.Date < day ? "↳ " : entry.Start.ToString("HH:mm ")) + entry.Title, "TextSecondary"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(4, 2) };
                ToolTip.SetTip(label, "Google Calendar · " + entry.Title); label.Click += (_, _) => selectEvent(entry); panel.Children.Add(label);
            }
            foreach (var slot in TaskSchedule.OnDay(vm.Tasks.Where(t => t.Status != WorkStatus.Cancelled), day))
            {
                panel.Children.Add(new ScheduledTaskCard(vm, slot.Task, editTask, slot.Schedule.Start));
            }
            var rule = vm.Data.Days.FirstOrDefault(d => d.Date.Date == day);
            var cell = new Border { Name = "CalendarDay" + day.ToString("yyyyMMdd"), ClipToBounds = true, MinWidth = 0, Child = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, Background = DesignTokens.Brush(isToday ? "AccentSoft" : past ? "Past" : day.Month == vm.CalendarDate.Month ? "Canvas" : "Panel"), BorderBrush = DesignTokens.Brush(isToday ? "Accent" : "Border"), BorderThickness = new Thickness(isToday ? 3 : 1), MinHeight = 76 };
            cell.PointerEntered += (_, _) => add.Opacity = 1; cell.PointerExited += (_, _) => { if (!add.IsFocused) add.Opacity = 0; };
            if (rule?.Unavailable == true) ToolTip.SetTip(cell, "Unavailable · manual placements may conflict");
            DragDrop.SetAllowDrop(cell, true);
            cell.AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = e.DataTransfer.Contains(TaskDragSupport.Format) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; });
            cell.AddHandler(DragDrop.DropEvent, (_, e) =>
            {
                if (e.DataTransfer.TryGetValue(TaskDragSupport.Format) is not string id || !TaskDragSupport.TryRead(id, out var taskId, out var previousStart)) return;
                var task = vm.Data.Tasks.SingleOrDefault(t => t.Id == taskId); if (task == null) return;
                try { vm.ManualSchedule(task, day.Add(previousStart?.TimeOfDay ?? task.ScheduledStart?.TimeOfDay ?? TimeSpan.FromHours(9)), previousStart); vm.Selected = task; } catch (Exception ex) { vm.Status = ex.Message; } e.Handled = true;
            });
            Grid.SetColumn(cell, i % 7); Grid.SetRow(cell, i / 7 + 1); grid.Children.Add(cell);
        }
    }
}
