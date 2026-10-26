using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ResearchTracker.Core;

namespace ResearchTracker.Desktop;

public sealed class AutoAssignWindow : Window
{
    public AutoAssignWindow(MainViewModel vm)
    {
        Title = "Auto assign"; Width = 540; Height = 580; MinWidth = 440; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"), Margin = new Thickness(20), RowSpacing = 12 }; Content = grid;
        grid.Children.Add(new TextBlock { Text = vm.WeekStart.ToString("MMM d") + " – " + vm.WeekStart.AddDays(6).ToString("MMM d, yyyy"), FontSize = DesignTokens.Section, FontWeight = FontWeight.SemiBold });
        var capacity = Scheduler.WeeklyCapacity(vm.Data, vm.WeekStart);
        var list = new StackPanel { Spacing = 8 }; var scroll = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var counters = new Dictionary<Guid, NumericUpDown>();
        foreach (var task in vm.Data.Tasks.Where(t => t.Status is WorkStatus.Todo or WorkStatus.InProgress).OrderByDescending(t => t.Priority).ThenBy(t => t.Title))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,84"), ColumnSpacing = 12 }; var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = task.Title, TextWrapping = TextWrapping.Wrap });
            var project = vm.Data.Projects.FirstOrDefault(p => p.Id == task.ProjectId);
            text.Children.Add(new TextBlock { Text = (project?.Title ?? "No project") + $" - {task.EffortHours:0.##}h each", FontSize = DesignTokens.Caption, Foreground = DesignTokens.Brush("TextMuted") }); row.Children.Add(text);
            var limit = Scheduler.DeadlineCountLimit(vm.Data, task, vm.WeekStart, DateTime.Now);
            var number = new NumericUpDown { Name = "AssignCount" + task.Id.ToString("N"), Minimum = 0, Maximum = limit, Increment = 1, FormatString = "0", Value = 0, VerticalAlignment = VerticalAlignment.Center }; counters.Add(task.Id, number); Grid.SetColumn(number, 1); row.Children.Add(number); list.Children.Add(row);
            Avalonia.Automation.AutomationProperties.SetName(number, "Weekly count for " + task.Title);
        }
        var totals = new WrapPanel { Orientation = Orientation.Horizontal }; Grid.SetRow(totals, 2); grid.Children.Add(totals);
        var total = new TextBlock { Name = "AssignTotalHours", FontSize = DesignTokens.Body, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 20, 0) }; totals.Children.Add(total);
        var taskTotal = new TextBlock { Name = "AssignTotalTasks", FontSize = DesignTokens.Body, FontWeight = FontWeight.SemiBold }; totals.Children.Add(taskTotal);
        var error = new TextBlock { Name = "AssignCountsError", TextWrapping = TextWrapping.Wrap, Foreground = DesignTokens.Brush("Danger"), FontSize = DesignTokens.Caption }; Grid.SetRow(error, 3); grid.Children.Add(error);
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 }; Grid.SetRow(buttons, 4); grid.Children.Add(buttons);
        var clear = new Button { Content = "Clear counts", HorizontalAlignment = HorizontalAlignment.Left }; clear.Click += (_, _) => { foreach (var counter in counters.Values) counter.Value = 0; }; buttons.Children.Add(clear);
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => Close(); Grid.SetColumn(cancel, 1); buttons.Children.Add(cancel);
        var assign = new Button { Name = "ConfirmAutoAssign", Content = "Assign", Classes = { "accent" } }; Grid.SetColumn(assign, 2); buttons.Children.Add(assign);
        Dictionary<Guid, int> Counts() => counters.ToDictionary(p => p.Key, p => (int)(p.Value.Value ?? 0));
        void Validate()
        {
            var hours = counters.Sum(p => (double)(p.Value.Value ?? 0) * vm.Data.Tasks.Single(t => t.Id == p.Key).EffortHours);
            total.Text = $"Total hours: {hours:0.##}";
            total.Foreground = DesignTokens.Brush(hours > capacity.Hours + .00001 ? "Danger" : "TextPrimary");
            var tasks = counters.Values.Sum(c => c.Value ?? 0);
            taskTotal.Text = $"Total tasks: {tasks:0}";
            taskTotal.Foreground = DesignTokens.Brush(tasks > capacity.Tasks ? "Danger" : "TextPrimary");
            var errors = counters.Values.Any(c => c.Value == null || c.Value != decimal.Truncate(c.Value.Value)) ? new List<string> { "Counts must be whole numbers." } : Scheduler.ValidateCounts(vm.Data, vm.WeekStart, Counts(), DateTime.Now);
            error.Text = string.Join("\n", errors); assign.IsEnabled = errors.Count == 0;
        }
        foreach (var counter in counters.Values) counter.ValueChanged += (_, _) => Validate();
        assign.Click += (_, _) => { Validate(); if (assign.IsEnabled) Close(Counts()); }; Validate();
    }
}
