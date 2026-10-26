using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ResearchTracker.Core;

namespace ResearchTracker.Desktop;

public sealed class WeekDayTimeline : ScrollViewer
{
    protected override Type StyleKeyOverride => typeof(ScrollViewer);
    public DateTime Day { get; }
    public WeekDayTimeline(MainViewModel vm, DayRule rule, Action<WorkTask> edit, Action<CalendarEvent> openEvent, double initialOffset)
    {
        Day = rule.Date.Date; Name = "DayTimeline" + Day.ToString("yyyyMMdd");
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; VerticalScrollBarVisibility = ScrollBarVisibility.Visible; ClipToBounds = true;
        var canvas = new Canvas { Name = "TimeSlots" + Day.ToString("yyyyMMdd"), Height = 24 * WeekTimeScale.PixelsPerHour, Background = DesignTokens.Brush("Canvas"), ClipToBounds = true }; Content = canvas;
        var slots = new List<(Control Control, double Start, double End, bool Timed)>();
        for (var halfHour = 0; halfHour < 48; halfHour++)
        {
            var top = halfHour * WeekTimeScale.PixelsPerHour / 2;
            var line = new Border { Height = 1, Background = DesignTokens.Brush("Border"), Opacity = halfHour % 2 == 0 ? 1 : .4, IsHitTestVisible = false };
            Canvas.SetTop(line, top); Canvas.SetLeft(line, 32); canvas.Children.Add(line); slots.Add((line, 0, 0, false));
            if (halfHour % 2 == 0)
            {
                var label = new TextBlock { Text = (halfHour / 2).ToString("00") + ":00", FontSize = 9, Foreground = DesignTokens.Brush("TextMuted"), IsHitTestVisible = false };
                Canvas.SetTop(label, top + 2); Canvas.SetLeft(label, 1); canvas.Children.Add(label);
            }
        }
        var tasks = TaskSchedule.OnDay(vm.Tasks.Where(t => t.Status is WorkStatus.Todo or WorkStatus.InProgress), Day).ToList();
        foreach (var slot in tasks)
        {
            var task = slot.Task; var time = slot.Schedule.Start; var start = Math.Max(0, (time - Day).TotalHours); var end = Math.Min(24, (time.AddHours(task.EffortHours) - Day).TotalHours);
            var card = new ScheduledTaskCard(vm, task, edit, time) { Height = Math.Max(38, (end - start) * WeekTimeScale.PixelsPerHour - 2) };
            Canvas.SetTop(card, start * WeekTimeScale.PixelsPerHour); canvas.Children.Add(card); slots.Add((card, start, end, true));
        }
        foreach (var entry in vm.CalendarCache.Events.Where(e => e.Overlaps(Day, Day.AddDays(1))).Where(e => e.TaskId == null || !tasks.Any(t => t.Task.Id == e.TaskId && t.Schedule.Start == e.Start && t.Schedule.Start.AddHours(t.Task.EffortHours) == e.End)).Where(e => string.IsNullOrWhiteSpace(vm.TaskQuery) || e.Title.Contains(vm.TaskQuery, StringComparison.OrdinalIgnoreCase)))
        {
            var start = Math.Max(0, (entry.Start - Day).TotalHours); var end = Math.Min(24, (entry.End - Day).TotalHours);
            var button = new Button { Content = new TextBlock { Text = entry.Title, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis }, Padding = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top, Height = Math.Max(28, (end - start) * WeekTimeScale.PixelsPerHour - 2), ClipToBounds = true };
            ToolTip.SetTip(button, "Google Calendar - " + entry.Title); button.Click += (_, _) => openEvent(entry);
            Canvas.SetTop(button, start * WeekTimeScale.PixelsPerHour); canvas.Children.Add(button); slots.Add((button, start, end, true));
        }
        var ghostText = new TextBlock { FontSize = 10, Foreground = DesignTokens.Brush("TextPrimary") };
        var ghost = new Border { Child = ghostText, Padding = new Thickness(3), Background = DesignTokens.Brush("AccentSoft"), BorderBrush = DesignTokens.Brush("Accent"), BorderThickness = new Thickness(1), IsVisible = false, IsHitTestVisible = false }; canvas.Children.Add(ghost);
        void LayoutSlots()
        {
            var width = Math.Max(1, canvas.Bounds.Width - 34); ghost.Width = width; Canvas.SetLeft(ghost, 33);
            foreach (var line in slots.Where(s => !s.Timed)) line.Control.Width = width;
            // Connected overlap groups share lanes, keeping every scheduled item accessible.
            var pending = slots.Where(s => s.Timed).OrderBy(s => s.Start).ToList();
            while (pending.Count > 0)
            {
                var group = new List<(Control Control, double Start, double End, bool Timed)> { pending[0] }; pending.RemoveAt(0); var end = group[0].End;
                while (pending.Count > 0 && pending[0].Start < end) { end = Math.Max(end, pending[0].End); group.Add(pending[0]); pending.RemoveAt(0); }
                var laneEnds = new List<double>(); var placed = new List<(Control Control, int Lane)>();
                foreach (var item in group) { var lane = laneEnds.FindIndex(e => e <= item.Start); if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(item.End); } else laneEnds[lane] = item.End; placed.Add((item.Control, lane)); }
                foreach (var item in placed) { item.Control.Width = Math.Max(1, width / laneEnds.Count - 2); Canvas.SetLeft(item.Control, 33 + item.Lane * width / laneEnds.Count); }
            }
        }
        canvas.SizeChanged += (_, _) => LayoutSlots();
        EventHandler? initializeScroll = null;
        initializeScroll = (_, _) =>
        {
            if (Viewport.Height <= 0 || Extent.Height <= 0) return;
            LayoutSlots(); Offset = new Vector(0, initialOffset); LayoutUpdated -= initializeScroll;
        };
        LayoutUpdated += initializeScroll;
        DragDrop.SetAllowDrop(canvas, true);
        canvas.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.TryGetValue(TaskDragSupport.Format) is not string id || !TaskDragSupport.TryRead(id, out var taskId, out var previousStart) || vm.Data.Tasks.FirstOrDefault(t => t.Id == taskId) is not { } task) { e.DragEffects = DragDropEffects.None; return; }
            var start = WeekTimeScale.TimeAt(Day, e.GetPosition(canvas).Y); ghostText.Text = start.ToString("HH:mm"); ghost.Height = Math.Max(20, Math.Min(task.EffortHours, 24 - start.TimeOfDay.TotalHours) * WeekTimeScale.PixelsPerHour); Canvas.SetTop(ghost, WeekTimeScale.Position(start)); ghost.IsVisible = true;
            var y = e.GetPosition(this).Y; if (y < 24) Offset = new Vector(0, Math.Max(0, Offset.Y - 16)); else if (y > Bounds.Height - 24) Offset = new Vector(0, Offset.Y + 16);
            e.DragEffects = DragDropEffects.Move; e.Handled = true;
        });
        canvas.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ghost.IsVisible = false);
        canvas.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            ghost.IsVisible = false; if (e.DataTransfer.TryGetValue(TaskDragSupport.Format) is not string id || !TaskDragSupport.TryRead(id, out var taskId, out var previousStart) || vm.Data.Tasks.FirstOrDefault(t => t.Id == taskId) is not { } task) return;
            try { vm.Save(); vm.ManualSchedule(task, WeekTimeScale.TimeAt(Day, e.GetPosition(canvas).Y), previousStart); } catch (Exception ex) { vm.Status = ex.Message; } e.Handled = true;
        });
    }
}
