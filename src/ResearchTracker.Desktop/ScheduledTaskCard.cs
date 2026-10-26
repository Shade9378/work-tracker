using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ResearchTracker.Core;

namespace ResearchTracker.Desktop;

public sealed class ScheduledTaskCard : Border
{
    public ScheduledTaskCard(MainViewModel vm, WorkTask task, Action<WorkTask> edit, DateTime? occurrence = null)
    {
        Name = "ScheduledTask" + task.Id.ToString("N");
        var project = vm.Data.Projects.FirstOrDefault(p => p.Id == task.ProjectId);
        Background = DesignTokens.Brush("Surface"); BorderBrush = project == null ? DesignTokens.Brush("Border") : ProjectColors.Marker(project);
        BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(DesignTokens.Radius); Padding = new Thickness(6); ClipToBounds = true; Cursor = new Cursor(StandardCursorType.Hand);
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock { Text = task.Title, FontSize = DesignTokens.Body, TextWrapping = TextWrapping.Wrap, MaxHeight = 32, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = DesignTokens.Brush("TextPrimary") });
        if (project != null) text.Children.Add(new TextBlock { Text = project.Title, FontSize = DesignTokens.Caption, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = DesignTokens.Brush("TextMuted") });
        var remove = new Button { Name = "UnscheduleTask" + task.Id.ToString("N"), Content = "×", Width = 22, Height = 22, MinHeight = 22, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = DesignTokens.Brush("TextMuted"), FontSize = 16 };
        ToolTip.SetTip(remove, "Remove from calendar"); Avalonia.Automation.AutomationProperties.SetName(remove, "Remove " + task.Title + " from calendar");
        remove.Click += (_, e) => { e.Handled = true; vm.Save(); TaskSchedule.Remove(task, occurrence ?? task.ScheduledStart); vm.Refresh(); };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") }; grid.Children.Add(text); Grid.SetColumn(remove, 1); grid.Children.Add(remove); Child = grid;
        Avalonia.Automation.AutomationProperties.SetName(this, task.Title + (project == null ? "" : " - " + project.Title));
        TaskDragSupport.Attach(this, vm, task, () => edit(task), occurrence ?? task.ScheduledStart);
    }
}
