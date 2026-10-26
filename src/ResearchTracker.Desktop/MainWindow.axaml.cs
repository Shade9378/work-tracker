using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using ResearchTracker.Core;
using ResearchTracker.Persistence;

namespace ResearchTracker.Desktop;
public partial class MainWindow : Window
{
    public MainWindow() : this(new MainViewModel(new WorkspaceStore(App.DataRoot))) { }
    private readonly MainViewModel vm;
    private readonly AppearanceStore appearanceStore;
    private readonly IStartupRegistration startupRegistration;
    private AppearancePreferences appearance;
    private readonly Dictionary<DateTime, double> dayScrollOffsets = [];
    private readonly MonthCalendarView monthCalendar;
    private bool refreshing;
    private bool updatingGraphScrollbars;
    private double inspectorWidth;
    private bool projectPanelsShown = true;
    private StackPanel? taskEditorPanel;
    private Window? taskEditorWindow;
    private StackPanel InspectorContent => taskEditorPanel ?? Inspector;
    public MainWindow(MainViewModel model, IStartupRegistration? startupRegistration = null)
    {
        this.startupRegistration = startupRegistration ?? new WindowsStartupRegistration(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WorkTracker.Desktop.exe"));
        vm = model; appearanceStore = new AppearanceStore(vm.Store.Root); appearance = appearanceStore.Load(); AppearanceManager.Apply(appearance);
        InitializeComponent(); DataContext = vm; GraphView.ViewModel = vm;
        monthCalendar = new MonthCalendarView(vm, ShowCalendarEvent, task => _ = OpenTaskEditor(task)); MonthHost.Content = monthCalendar;
        GraphOverlay.Editor = GraphView;
        GraphView.ViewportUpdated += (_, _) => UpdateGraphScrollbars();
        GraphHorizontalScroll.Scroll += (_, e) => { if (!updatingGraphScrollbars) GraphView.ViewportLocation = new Point(e.NewValue, GraphView.ViewportLocation.Y); };
        GraphVerticalScroll.Scroll += (_, e) => { if (!updatingGraphScrollbars) GraphView.ViewportLocation = new Point(GraphView.ViewportLocation.X, e.NewValue); };
        GraphView.DeleteRequested += () => _ = ConfirmDelete();
        GraphView.RenameRequested += FocusTitle;
        GraphView.HelpRequested += () => _ = OpenShortcuts();
        GraphView.ModeChanged += () => { SelectModeButton.Classes.Set("accent", GraphView.Mode == GraphInteractionMode.Select); MoveModeButton.Classes.Set("accent", GraphView.Mode == GraphInteractionMode.Move); };
        AddHandler(KeyDownEvent, WorkspaceKeyDown, RoutingStrategies.Tunnel);
        vm.Refreshed += RenderLists; vm.SelectionChanged += () => { BuildInspector(); GraphView.InvalidateVisual(); };
        MainTabs.SelectionChanged += (_, e) => { if (e.Source == MainTabs) UpdateWorkspacePanels(); };
        SizeChanged += (_, _) => { UpdateWorkspacePanels(); ConstrainBacklog(); };
        PlanGrid.SizeChanged += (_, _) => ConstrainBacklog();
        Opened += (_, _) => { SetInspectorWidth(); Avalonia.Threading.Dispatcher.UIThread.Post(GraphView.Fit, Avalonia.Threading.DispatcherPriority.Background); };
        RenderLists(); BuildInspector();
        Closing += (_, e) => { vm.Save(); if (vm.Status.StartsWith("SAVE FAILED")) e.Cancel = true; };
    }
    private void SetInspectorWidth()
    {
        if (Bounds.Width > 0) { inspectorWidth = Bounds.Width / 5; if (MainTabs.SelectedIndex == 0) WorkspaceGrid.ColumnDefinitions[3].Width = new GridLength(inspectorWidth); UpdateWorkspacePanels(); }
    }
    private void UpdateWorkspacePanels()
    {
        var projects = MainTabs.SelectedIndex == 0; var column = WorkspaceGrid.ColumnDefinitions[3];
        if (!projects && projectPanelsShown && column.Width.Value > 0) inspectorWidth = column.Width.Value;
        ProjectSidebar.IsVisible = projects; InspectorPanel.IsVisible = projects; InspectorSplitter.IsVisible = projects;
        WorkspaceGrid.ColumnDefinitions[0].Width = new GridLength(projects ? 256 : 0); WorkspaceGrid.ColumnDefinitions[2].Width = new GridLength(projects ? 4 : 0);
        column.MinWidth = projects ? 220 : 0; column.MaxWidth = projects ? Math.Max(220, Math.Min(440, Bounds.Width * .4)) : double.PositiveInfinity;
        if (projects)
        {
            var width = projectPanelsShown && column.Width.Value > 0 ? column.Width.Value : inspectorWidth > 0 ? inspectorWidth : Bounds.Width / 5;
            column.Width = new GridLength(Math.Clamp(width, 220, column.MaxWidth));
        }
        else column.Width = new GridLength(0);
        projectPanelsShown = projects;
    }
    private void ConstrainBacklog()
    {
        if (PlanGrid.Bounds.Height <= 0) return;
        PlanGrid.RowDefinitions[2].MinHeight = 220; PlanGrid.RowDefinitions[4].MinHeight = 140;
        var maximum = Math.Max(140, PlanGrid.Bounds.Height - 48 - 40 - 5 - 220);
        if (PlanGrid.RowDefinitions[4].Height.Value > maximum) PlanGrid.RowDefinitions[4].Height = new GridLength(maximum);
    }
    private void UpdateGraphScrollbars()
    {
        if (GraphView.Bounds.Width <= 0 || GraphView.Bounds.Height <= 0 || updatingGraphScrollbars) return;
        updatingGraphScrollbars = true;
        try
        {
            var bounds = GraphView.ContentBounds(); var origin = GraphView.ViewportLocation; var width = GraphView.Bounds.Width / GraphView.ViewportZoom; var height = GraphView.Bounds.Height / GraphView.ViewportZoom;
            void Update(ScrollBar bar, double minimum, double maximum, double value, double size)
            {
                bar.Minimum = Math.Min(minimum, value); bar.Maximum = Math.Max(Math.Max(bar.Minimum, maximum - size), value); bar.ViewportSize = size; bar.Value = value;
                bar.SmallChange = 48 / GraphView.ViewportZoom; bar.LargeChange = size * .8; bar.IsVisible = bar.Maximum - bar.Minimum > 1;
            }
            Update(GraphHorizontalScroll, bounds.Left, bounds.Right, origin.X, width); Update(GraphVerticalScroll, bounds.Top, bounds.Bottom, origin.Y, height);
        }
        finally { updatingGraphScrollbars = false; }
    }
    private static TextBlock Text(string text, double size = DesignTokens.Body, string color = "TextPrimary") => new() { Text = text, FontSize = size, Foreground = DesignTokens.Brush(color), TextWrapping = TextWrapping.Wrap };
    private static TextBlock Label(string text) => new() { Text = text.ToUpperInvariant(), Classes = { "label" } };
    private static Button Button(string label, Action action)
    {
        var button = new Button { Content = label }; button.Click += (_, _) => action(); return button;
    }
    private static TextBox Edit(object source, string property, bool multiline = false)
    {
        var box = new TextBox { AcceptsReturn = multiline, VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 56 : 28 };
        box.Bind(TextBox.TextProperty, new Binding(property) { Source = source, Mode = BindingMode.TwoWay }); return box;
    }
    private static ComboBox EnumEdit<T>(object source, string property) where T : struct, Enum
    {
        var combo = new ComboBox { ItemsSource = Enum.GetValues<T>(), HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(property) { Source = source, Mode = BindingMode.TwoWay }); return combo;
    }
    private void Guard(Action action)
    {
        try { action(); } catch (Exception ex) { vm.Status = ex.Message; }
    }
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); } catch (Exception ex) { vm.Status = ex.Message; }
    }
    private void RenderLists()
    {
        if (refreshing) return; refreshing = true;
        try
        {
            GraphView.InvalidateVisual(); TaskList.Children.Clear();
            ProjectColorOptions.Children.Clear();
            if (vm.SelectedProject is { } currentProject)
                foreach (var hex in GraphColors.ProjectPalette)
                {
                    var swatch = new Button { Name = "ProjectColor" + hex[1..], Width = 26, Height = 24, MinHeight = 24, Padding = new Thickness(0), Background = new SolidColorBrush(Color.Parse(hex)), BorderBrush = DesignTokens.Brush(currentProject.DisplayColor == hex ? "TextPrimary" : "Border"), BorderThickness = new Thickness(currentProject.DisplayColor == hex ? 2 : 1) };
                    ToolTip.SetTip(swatch, hex); Avalonia.Automation.AutomationProperties.SetName(swatch, "Project color " + hex); swatch.Click += (_, _) => { currentProject.ColorHex = hex; vm.Refresh(); BuildInspector(); GraphView.RefreshTheme(); }; ProjectColorOptions.Children.Add(swatch);
                }
            if (vm.SelectedProject is { } customProject) ProjectColorOptions.Children.Add(Button("More colors…", () => _ = ChooseProjectColor(customProject)));
            ProjectActivity.Children.Clear();
            foreach (var t in vm.Data.Tasks.Where(t => t.ProjectId == vm.SelectedProject?.Id && t.Status is WorkStatus.Todo or WorkStatus.InProgress).OrderByDescending(t => t.Priority)) ProjectActivity.Children.Add(TaskCard(t, false));
            if (ProjectActivity.Children.Count == 0) ProjectActivity.Children.Add(Text("No open tasks in this project.", DesignTokens.Body, "TextMuted"));
            foreach (var t in vm.Tasks.OrderBy(t => t.Status).ThenByDescending(t => t.Priority).ThenBy(t => t.Deadline)) TaskList.Children.Add(TaskCard(t, false));
            if (TaskList.Children.Count == 0) TaskList.Children.Add(Text("No matching tasks. Create a task or plan an action from the graph.", 14, "TextMuted"));
            WeekLabel.Text = vm.MonthView ? vm.CalendarDate.ToString("MMMM yyyy") : vm.WeekStart.ToString("MMM d") + " – " + vm.WeekStart.AddDays(6).ToString("MMM d, yyyy");
            WeekScroll.IsVisible = !vm.MonthView; MonthHost.IsVisible = vm.MonthView;
            WeekViewButton.Classes.Set("accent", !vm.MonthView); MonthViewButton.Classes.Set("accent", vm.MonthView);
            if (vm.MonthView) monthCalendar.Render();
            CalendarCacheStatus.Text = vm.CalendarCache.RefreshedAt is { } fetched ? "Cached · " + fetched.LocalDateTime.ToString("MMM d HH:mm") : "";
            ToolTip.SetTip(CalendarCacheStatus, "Google events are cached offline. Refresh visible dates from Google Calendar to get changes.");
            foreach (var timeline in DayColumns.GetVisualDescendants().OfType<WeekDayTimeline>().Where(t => t.Bounds.Height > 0)) dayScrollOffsets[timeline.Day] = timeline.Offset.Y;
            DayColumns.Children.Clear();
            foreach (var day in vm.Days)
            {
                var stack = new StackPanel { Spacing = DesignTokens.Space1 };
                var today = day.Date.Date == DateTime.Today; var past = day.Date.Date < DateTime.Today;
                var dayHeading = new StackPanel { Spacing = 2 }; var weekday = Text(day.Date.ToString("ddd").ToUpperInvariant(), 11, past ? "PastText" : "TextPrimary"); var dateLabel = Text(day.Date.ToString("dd MMM"), DesignTokens.Section, past ? "PastText" : "TextPrimary");
                if (today) { weekday.Foreground = DesignTokens.Brush("OnAccent"); dateLabel.Foreground = DesignTokens.Brush("OnAccent"); }
                dayHeading.Children.Add(weekday); dayHeading.Children.Add(dateLabel); stack.Children.Add(new Border { Name = "WeekDate" + day.Date.ToString("yyyyMMdd"), Child = dayHeading, Padding = new Thickness(6), Background = DesignTokens.Brush(today ? "Accent" : past ? "Past" : "Panel") });
                stack.Children.Add(Text(day.Unavailable ? "Unavailable" : $"{day.StartHour:0.##}–{day.EndHour:0.##} · {day.MaxHours:0.##}h capacity", 11, "TextMuted"));
                var constraints = Button("Constraints", () => _ = EditDay(day)); constraints.Name = "Constraints" + day.Date.ToString("yyyyMMdd"); constraints.IsEnabled = !past; stack.Children.Add(constraints);
                var tasks = TaskSchedule.OnDay(vm.Data.Tasks.Where(t => t.Status is WorkStatus.Todo or WorkStatus.InProgress), day.Date).ToList();
                stack.Children.Add(Text($"{tasks.Count}/{day.MaxTasks} tasks - {tasks.Sum(t => t.Task.EffortHours):0.##}/{day.MaxHours:0.##}h", 11, "TextMuted"));
                var timeline = new WeekDayTimeline(vm, day, t => _ = OpenTaskEditor(t), ShowCalendarEvent, dayScrollOffsets.GetValueOrDefault(day.Date.Date, 8 * WeekTimeScale.PixelsPerHour));
                var dayGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") }; dayGrid.Children.Add(stack); Grid.SetRow(timeline, 1); dayGrid.Children.Add(timeline);
                var border = new Border { Name = "WeekDay" + day.Date.ToString("yyyyMMdd"), MinWidth = 0, ClipToBounds = true, Padding = new Thickness(4), CornerRadius = new CornerRadius(DesignTokens.Radius), Background = DesignTokens.Brush(past ? "Past" : "Panel"), BorderBrush = DesignTokens.Brush(today ? "Accent" : "Border"), BorderThickness = new Thickness(today ? 2 : 1), Child = dayGrid };
                Grid.SetColumn(border, DayColumns.Children.Count); DayColumns.Children.Add(border);
            }
            Backlog.Children.Clear();
            foreach (var task in vm.Tasks.OrderByDescending(t => t.Priority).ThenBy(t => t.Title)) Backlog.Children.Add(TaskCard(task, true));
            var invalidDays = vm.Days.Where(d => !Scheduler.ValidRule(d)).Select(d => d.Label + ": invalid constraints");
            var issues = vm.Data.Tasks.Where(t => t.Status is WorkStatus.Todo or WorkStatus.InProgress).SelectMany(t => vm.Scheduler.Conflicts(vm.Data, t).Select(message => new ScheduleIssue(t.Id, message))).ToList();
            ConflictSummary.Text = string.Join("\n", invalidDays.Concat(issues.Where(i => vm.Data.Tasks.Any(t => t.Id == i.TaskId && t.ScheduledStart != null)).Select(i => vm.Data.Tasks.First(t => t.Id == i.TaskId).Title + ": " + i.Message)));
            ConflictSummary.IsVisible = !string.IsNullOrEmpty(ConflictSummary.Text);
            UpdateGraphScrollbars();
        }
        finally { refreshing = false; }
    }
    private Control TaskCard(WorkTask task, bool compact)
    {
        var stack = new StackPanel { Spacing = DesignTokens.Space1 };
        var conflicts = task.ScheduledStart == null || task.Status is WorkStatus.Done or WorkStatus.Cancelled ? new List<string>() : vm.Scheduler.Conflicts(vm.Data, task);
        var placementLabel = Text(TaskSchedule.Placement(task, conflicts), DesignTokens.Caption, task.Status == WorkStatus.Done ? "Success" : task.Status == WorkStatus.Cancelled ? "TextMuted" : task.ScheduledStart == null || conflicts.Count > 0 ? "Danger" : "Success");
        var unscheduled = task.ScheduledStart == null && task.Status is WorkStatus.Todo or WorkStatus.InProgress;
        placementLabel.FontWeight = unscheduled ? FontWeight.Normal : FontWeight.Bold;
        if (unscheduled) placementLabel.Text = "Unscheduled";
        stack.Children.Add(Text(task.Title, compact ? DesignTokens.Body : DesignTokens.Section));
        if (!compact && task.Summary.Length > 0) stack.Children.Add(Text(task.Summary, 12, "TextSecondary"));
        var priorityColor = DesignTokens.Brush(task.Priority switch { Priority.Normal => "Success", Priority.High => "Warning", Priority.Urgent => "Danger", _ => "TextMuted" });
        var priorityIcon = task.Priority switch
        {
            Priority.High => "F0 M12,2 L24,23 L0,23 Z M11,8 L13,8 L13,16 L11,16 Z M11,18 L13,18 L13,20 L11,20 Z",
            Priority.Urgent => "F0 M12,0 A12,12 0 1 1 12,24 A12,12 0 1 1 12,0 Z M11,5 L13,5 L13,14 L11,14 Z M11,17 L13,17 L13,19 L11,19 Z",
            Priority.Normal => "F0 M12,0 A12,12 0 1 1 12,24 A12,12 0 1 1 12,0 Z M5,12 L7,10 L10,13 L17,6 L19,8 L10,17 Z",
            _ => "F0 M12,0 A12,12 0 1 1 12,24 A12,12 0 1 1 12,0 Z M5,11 L19,11 L19,13 L5,13 Z"
        };
        var metadata = new WrapPanel { Orientation = Orientation.Horizontal };
        var priority = new StackPanel { Name = "TaskPriority" + task.Id.ToString("N"), Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        priority.Children.Add(new PathIcon { Width = 12, Height = 12, Data = Geometry.Parse(priorityIcon), Foreground = priorityColor });
        var priorityLabel = Text(task.Priority.ToString().ToUpperInvariant(), DesignTokens.Caption); priorityLabel.Foreground = priorityColor; priority.Children.Add(priorityLabel); metadata.Children.Add(priority);
        metadata.Children.Add(Text($" - {task.EffortHours:0.##}h - " + (task.Status == WorkStatus.Done ? "DONE" : task.Status.ToString()) + (task.EdgeId == null ? "" : " - graph action"), DesignTokens.Caption, "TextMuted")); stack.Children.Add(metadata);
        if (task.Deadline != null) stack.Children.Add(Text("Due " + task.Deadline.Value.ToString("MMM d, HH:mm"), DesignTokens.Caption, "Warning"));
        var project = vm.Data.Projects.FirstOrDefault(p => p.Id == task.ProjectId);
        if (project != null) { var projectLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; projectLine.Children.Add(new Border { Width = 4, Height = 12, Background = ProjectColors.Marker(project) }); projectLine.Children.Add(Text(project.Title, DesignTokens.Caption, "TextSecondary")); stack.Children.Add(projectLine); }
        stack.Children.Add(Button("Edit task", () => _ = OpenTaskEditor(task)));
        var delete = new Button { Name = "DeleteTask" + task.Id.ToString("N"), Content = new PathIcon { Width = 16, Height = 16, Data = Geometry.Parse("M8,2 L16,2 L17,5 L22,5 L22,7 L2,7 L2,5 L7,5 Z M5,9 L19,9 L18,22 L6,22 Z M9,11 L9,20 L11,20 L11,11 Z M13,11 L13,20 L15,20 L15,11 Z") }, Foreground = DesignTokens.Brush("Danger"), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Width = 28, Height = 28, Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0) };
        ToolTip.SetTip(delete, "Delete task · Ctrl+Z to undo"); Avalonia.Automation.AutomationProperties.SetName(delete, "Delete task " + task.Title);
        delete.Click += (_, e) => { e.Handled = true; Guard(() => { vm.Save(); vm.Selected = task; vm.DeleteSelected(); }); };
        var complete = new Button { Name = "CompleteTask" + task.Id.ToString("N"), Content = new PathIcon { Width = 16, Height = 16, Data = Geometry.Parse("M9,16 L4,11 L2,13 L9,20 L22,7 L20,5 Z") }, Foreground = DesignTokens.Brush("Success"), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Width = 28, Height = 28, Padding = new Thickness(6), IsEnabled = task.Status is WorkStatus.Todo or WorkStatus.InProgress };
        ToolTip.SetTip(complete, task.EdgeId == null ? "Complete task" : "Complete action and record outcome"); Avalonia.Automation.AutomationProperties.SetName(complete, "Complete task " + task.Title);
        complete.Click += async (_, e) => { e.Handled = true; await CompleteTask(task); };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top }; actions.Children.Add(complete); actions.Children.Add(delete);
        Control placementControl = placementLabel;
        if (unscheduled)
        {
            var warning = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 }; warning.Children.Add(new PathIcon { Width = 12, Height = 12, Foreground = DesignTokens.Brush("Danger"), Data = Geometry.Parse("F0 M12,2 L24,23 L0,23 Z M11,8 L13,8 L13,16 L11,16 Z M11,18 L13,18 L13,20 L11,20 Z") }); warning.Children.Add(placementLabel); placementControl = warning;
        }
        if (task.Status == WorkStatus.Done)
        {
            var doneLabel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 }; doneLabel.Children.Add(new PathIcon { Width = 12, Height = 12, Foreground = DesignTokens.Brush("Success"), Data = Geometry.Parse("M9,16 L4,11 L2,13 L9,20 L22,7 L20,5 Z") }); doneLabel.Children.Add(placementLabel); placementControl = doneLabel;
        }
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") }; header.Children.Add(placementControl); Grid.SetColumn(actions, 1); header.Children.Add(actions);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") }; layout.Children.Add(header); Grid.SetRow(stack, 1); layout.Children.Add(stack);
        var border = new Border { Name = "TaskCard" + task.Id.ToString("N"), Background = DesignTokens.Brush("Surface"), BorderBrush = project != null ? ProjectColors.Marker(project) : DesignTokens.Brush(conflicts.Count > 0 ? "Danger" : "Border"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(DesignTokens.Radius), Padding = new Thickness(12), Child = layout, Cursor = new Cursor(StandardCursorType.Hand) };
        TaskDragSupport.Attach(border, vm, task);
        return border;
    }
    private async Task CompleteTask(WorkTask task)
    {
        if (task.EdgeId == null) { Guard(() => { vm.Save(); vm.Graph.Complete(task, ""); vm.Refresh(); }); return; }
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        var dialog = new Window { Title = "Complete action", Width = 420, Height = 210, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        panel.Children.Add(Label("Resulting state")); var outcome = new TextBox { Name = "CompleteOutcomeTitle", PlaceholderText = "Name the resulting state" }; panel.Children.Add(outcome);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 }; panel.Children.Add(buttons);
        buttons.Children.Add(Button("Cancel", () => dialog.Close()));
        var done = Button("Complete", () => Guard(() => { vm.Save(); vm.Graph.Complete(task, outcome.Text ?? ""); vm.Refresh(); dialog.Close(); })); done.Name = "ConfirmTaskComplete"; done.IsEnabled = false; buttons.Children.Add(done);
        outcome.TextChanged += (_, _) => done.IsEnabled = !string.IsNullOrWhiteSpace(outcome.Text); dialog.Opened += (_, _) => outcome.Focus(); await dialog.ShowDialog(this);
    }
    private void BuildInspector()
    {
        InspectorContent.Children.Clear();
        var entity = vm.Selected;
        InspectorContent.Children.Add(Text("DETAILS", DesignTokens.Caption, "Accent"));
        if (entity == null)
        {
            InspectorContent.Children.Add(Text("No selection", DesignTokens.Section));
            return;
        }
        if (vm.GraphSelection.Count > 1) { InspectorContent.Children.Clear(); return; }
        if (entity is not GraphNode and not GraphEdge) InspectorContent.Children.Add(Text(entity is Project ? "Project information" : "Actionable task", DesignTokens.Section));
        InspectorContent.Children.Add(Label("Title")); var titleEditor = Edit(entity, nameof(Entity.Title)); titleEditor.Name = "EntityTitle"; InspectorContent.Children.Add(titleEditor);
        InspectorContent.Children.Add(Label("Summary / description")); InspectorContent.Children.Add(Edit(entity, nameof(Entity.Summary), true));
        if (entity is GraphNode node) { InspectorContent.Children.Add(Label("State type")); InspectorContent.Children.Add(EnumEdit<NodeKind>(node, nameof(GraphNode.Kind))); }
        if (entity is GraphEdge edgeEntity) BuildEdgeEditor(edgeEntity);
        if (entity is WorkTask task) BuildTaskEditor(task);
        foreach (var block in entity.Content.ToList()) BuildBlock(entity, block);
        var add = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var kind in Enum.GetValues<BlockKind>())
        {
            var button = Button("+ " + kind, () =>
            {
                if (kind == BlockKind.Attachment) { _ = Attach(entity); return; }
                entity.Content.Add(new ContentBlock { Kind = BlockKind.Text }); vm.Save(); BuildInspector();
            }); button.Margin = new Thickness(0, 0, DesignTokens.Space1, DesignTokens.Space1); add.Children.Add(button);
        }
        InspectorContent.Children.Add(add);
        if (entity is not Project)
        {
            var delete = Button("Delete " + (entity is WorkTask ? "task" : entity is GraphEdge ? "action" : "state"), () => _ = ConfirmDelete());
            if (entity is GraphNode or GraphEdge)
            {
                var label = entity is GraphNode ? "Delete state" : "Delete action";
                delete.Name = entity is GraphNode ? "DeleteStateButton" : "DeleteActionButton"; delete.Foreground = DesignTokens.Brush("Danger"); delete.BorderBrush = DesignTokens.Brush("Danger");
                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                content.Children.Add(new PathIcon { Width = 16, Height = 16, Foreground = DesignTokens.Brush("Danger"), Data = Geometry.Parse("M8,2 L16,2 L17,5 L22,5 L22,7 L2,7 L2,5 L7,5 Z M5,9 L19,9 L18,22 L6,22 Z M9,11 L9,20 L11,20 L11,11 Z M13,11 L13,20 L15,20 L15,11 Z") });
                content.Children.Add(Text(label, DesignTokens.Body, "Danger")); delete.Content = content;
                Avalonia.Automation.AutomationProperties.SetName(delete, label);
            }
            InspectorContent.Children.Add(delete);
        }
    }
    private void BuildEdgeEditor(GraphEdge edge)
    {
        InspectorContent.Children.Add(Label("Start / source state"));
        var sources = new List<object> { "Free on canvas" }; sources.AddRange(vm.Nodes);
        var source = new ComboBox { ItemsSource = sources, SelectedItem = edge.SourceId == null ? sources[0] : vm.Nodes.FirstOrDefault(n => n.Id == edge.SourceId), HorizontalAlignment = HorizontalAlignment.Stretch };
        source.SelectionChanged += (_, _) => Guard(() => { var point = GraphGeometry.Endpoints(vm.Data, edge).Source; vm.Graph.MoveEndpoint(edge, EdgeEnd.Source, source.SelectedItem as GraphNode, point.X, point.Y); vm.Refresh(); }); InspectorContent.Children.Add(source);
        InspectorContent.Children.Add(Label("Resulting state"));
        var targets = new List<object> { "Free / unknown result" }; targets.AddRange(vm.Nodes);
        var target = new ComboBox { ItemsSource = targets, SelectedItem = edge.TargetId == null ? targets[0] : vm.Nodes.FirstOrDefault(n => n.Id == edge.TargetId), HorizontalAlignment = HorizontalAlignment.Stretch };
        target.SelectionChanged += (_, _) => Guard(() => { var point = GraphGeometry.Endpoints(vm.Data, edge).Target; vm.Graph.MoveEndpoint(edge, EdgeEnd.Target, target.SelectedItem as GraphNode, point.X, point.Y); vm.Refresh(); BuildInspector(); }); InspectorContent.Children.Add(target);
        var linked = vm.Data.Tasks.FirstOrDefault(t => t.EdgeId == edge.Id);
        if (linked != null) InspectorContent.Children.Add(Button("Open linked task →", () => { MainTabs.SelectedIndex = 1; vm.Selected = linked; }));
    }
    private void BuildTaskEditor(WorkTask task)
    {
        InspectorContent.Children.Add(Label("Project"));
        var projectOptions = new List<object> { "No project" }; projectOptions.AddRange(vm.Data.Projects);
        var projects = new ComboBox { Name = "TaskProjectPicker", ItemsSource = projectOptions, SelectedItem = vm.Data.Projects.FirstOrDefault(p => p.Id == task.ProjectId) as object ?? projectOptions[0], IsEnabled = task.EdgeId == null, HorizontalAlignment = HorizontalAlignment.Stretch };
        projects.SelectionChanged += (_, _) => { task.ProjectId = (projects.SelectedItem as Project)?.Id; vm.Refresh(); }; InspectorContent.Children.Add(projects);
        InspectorContent.Children.Add(Label("Priority")); InspectorContent.Children.Add(EnumEdit<Priority>(task, nameof(WorkTask.Priority)));
        InspectorContent.Children.Add(Label("Status"));
        var status = new ComboBox { ItemsSource = new[] { WorkStatus.Todo, WorkStatus.InProgress, WorkStatus.Cancelled }, SelectedItem = task.Status, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (task.Status == WorkStatus.Done) InspectorContent.Children.Add(Text("Completed · choose Todo to reopen", 12, "Accent"));
        status.SelectionChanged += (_, _) => { if (status.SelectedItem is WorkStatus s) { task.Status = s; vm.Refresh(); } }; InspectorContent.Children.Add(status);
        InspectorContent.Children.Add(Label("Estimated effort (hours)"));
        var effort = new NumericUpDown { Minimum = .25m, Maximum = 168m, Increment = .25m, Value = (decimal)task.EffortHours, FormatString = "0.##" };
        effort.ValueChanged += (_, _) => { if (effort.Value is { } v) { task.EffortHours = (double)v; vm.Refresh(); } }; InspectorContent.Children.Add(effort);
        InspectorContent.Children.Add(Label("Deadline (local time, blank for none)"));
        var deadline = new TextBox { PlaceholderText = "yyyy-MM-dd HH:mm", Text = task.Deadline?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "" };
        deadline.LostFocus += (_, _) => Guard(() =>
        {
            if (string.IsNullOrWhiteSpace(deadline.Text)) task.Deadline = null;
            else if (DateTime.TryParseExact(deadline.Text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) task.Deadline = new DateTimeOffset(date);
            else throw new InvalidOperationException("Deadline format: yyyy-MM-dd HH:mm. The previous deadline was retained."); vm.Refresh();
        }); InspectorContent.Children.Add(deadline);
        InspectorContent.Children.Add(Label("Dependencies"));
        foreach (var id in task.Dependencies.ToList())
        {
            var dep = vm.Data.Tasks.First(t => t.Id == id);
            InspectorContent.Children.Add(Text(dep.Title + " · " + dep.Status, 11));
            InspectorContent.Children.Add(Button("Remove dependency", () => { task.Dependencies.Remove(id); vm.Refresh(); BuildInspector(); }));
        }
        var picker = new ComboBox { ItemsSource = vm.Data.Tasks.Where(t => t.Id != task.Id && !task.Dependencies.Contains(t.Id)).ToList(), PlaceholderText = "Choose prerequisite", HorizontalAlignment = HorizontalAlignment.Stretch };
        InspectorContent.Children.Add(picker); InspectorContent.Children.Add(Button("Add dependency", () => Guard(() => { if (picker.SelectedItem is WorkTask dep) { vm.Graph.AddDependency(task, dep); vm.Refresh(); BuildInspector(); } })));
        InspectorContent.Children.Add(Label("Manual schedule (local time)"));
        var when = new TextBox { PlaceholderText = "yyyy-MM-dd HH:mm", Text = task.ScheduledStart?.ToString("yyyy-MM-dd HH:mm") ?? "" }; InspectorContent.Children.Add(when);
        InspectorContent.Children.Add(Button("Place / override constraints", () => Guard(() =>
        {
            if (task.Status is WorkStatus.Done or WorkStatus.Cancelled) throw new InvalidOperationException("Reopen the task before scheduling.");
            if (!DateTime.TryParseExact(when.Text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw new InvalidOperationException("Schedule format: yyyy-MM-dd HH:mm.");
            if (vm.Data.Days.All(d => d.Date != date.Date)) vm.Data.Days.Add(new DayRule { Date = date.Date });
            vm.ManualSchedule(task, date); BuildInspector();
        })));
        foreach (var schedule in TaskSchedule.Occurrences(task).ToArray()) InspectorContent.Children.Add(Button("Remove " + schedule.Start.ToString("ddd, MMM d HH:mm"), () => { TaskSchedule.Remove(task, schedule.Start); vm.Refresh(); BuildInspector(); }));
        InspectorContent.Children.Add(Button("Unschedule all days", () => { TaskSchedule.Remove(task); vm.Refresh(); BuildInspector(); }));
        if (task.ScheduledStart != null) InspectorContent.Children.Add(Button("Send task to Google Calendar…", () => _ = OpenGoogleCalendar(task)));
        InspectorContent.Children.Add(Label(task.EdgeId == null ? "Complete task" : "Record resulting state"));
        var outcome = new TextBox { PlaceholderText = "Title of the resulting state" }; if (task.EdgeId != null) InspectorContent.Children.Add(outcome);
        InspectorContent.Children.Add(Button("Complete" + (task.EdgeId == null ? " task" : " & record outcome"), () => Guard(() => { var result = vm.Graph.Complete(task, outcome.Text ?? ""); vm.Refresh(); if (result != null) vm.Selected = result; else BuildInspector(); })));
        if (task.EdgeId is { } edgeId) InspectorContent.Children.Add(Button("Open graph action →", () => NavigateToGraph(vm.Data.Edges.Single(e => e.Id == edgeId))));
    }
    private void BuildBlock(Entity entity, ContentBlock block)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(Text(block.Kind.ToString().ToUpperInvariant(), DesignTokens.Caption, "TextSecondary"));
        if (block.Kind == BlockKind.Attachment)
        {
            panel.Children.Add(Text(block.Text, 11));
            panel.Children.Add(Button("Save a copy…", () => _ = SaveAttachment(block)));
        }
        else
        {
            var box = new TextBox { Text = block.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, VerticalContentAlignment = VerticalAlignment.Top };
            var preview = new StackPanel { Spacing = DesignTokens.Space1 };
            void Preview() { preview.Children.Clear(); ContentRenderer.Render(preview, block); }
            box.TextChanged += (_, _) => { block.Text = box.Text ?? ""; vm.Status = "Notes changed · autosaving"; vm.QueueSave(); Preview(); };
            if (block.Kind == BlockKind.Text)
            {
                var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignTokens.Space1 };
                foreach (var format in new[] { ("Bold", "**"), ("Italic", "*"), ("Heading", "# ") })
                    tools.Children.Add(Button(format.Item1, () =>
                    {
                        var start = Math.Min(box.SelectionStart, box.SelectionEnd); var length = Math.Abs(box.SelectionEnd - box.SelectionStart); var text = box.Text ?? "";
                        var selected = text.Substring(start, length); box.Text = text[..start] + format.Item2 + selected + (format.Item1 == "Heading" ? "" : format.Item2) + text[(start + length)..]; box.Focus();
                    }));
                panel.Children.Add(tools);
            }
            panel.Children.Add(box); panel.Children.Add(preview); Preview();
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        actions.Children.Add(Button("↑", () => { var i = entity.Content.IndexOf(block); if (i > 0) { entity.Content.RemoveAt(i); entity.Content.Insert(i - 1, block); vm.Save(); BuildInspector(); } }));
        var removeBlock = Button("", () => { vm.Save(); entity.Content.Remove(block); vm.Save(); BuildInspector(); });
        removeBlock.Name = "RemoveBlock" + block.Id.ToString("N");
        removeBlock.Content = new PathIcon { Width = 16, Height = 16, Data = Geometry.Parse("M8,2 L16,2 L17,5 L22,5 L22,7 L2,7 L2,5 L7,5 Z M5,9 L19,9 L18,22 L6,22 Z M9,11 L9,20 L11,20 L11,11 Z M13,11 L13,20 L15,20 L15,11 Z") };
        removeBlock.Foreground = DesignTokens.Brush("Danger"); removeBlock.Background = Brushes.Transparent; removeBlock.BorderThickness = new Thickness(0); removeBlock.Width = 28; removeBlock.Height = 28; removeBlock.Padding = new Thickness(6);
        ToolTip.SetTip(removeBlock, "Remove block"); Avalonia.Automation.AutomationProperties.SetName(removeBlock, "Remove block");
        actions.Children.Add(removeBlock); panel.Children.Add(actions);
        InspectorContent.Children.Add(new Border { Child = panel, BorderBrush = DesignTokens.Brush("Border"), BorderThickness = new Thickness(0, 0, 0, 1) });
    }
    private async Task Attach(Entity entity) => await GuardAsync(async () =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Attach file", AllowMultiple = true });
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath() ?? throw new IOException("Select a local file.");
            var name = vm.Store.AddAttachment(path); entity.Content.Add(new ContentBlock { Kind = BlockKind.Attachment, Text = file.Name, FileName = name });
        }
        vm.Save(); if (vm.Selected == entity) BuildInspector();
    });
    private async Task SaveAttachment(ContentBlock block) => await GuardAsync(async () =>
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = Path.GetFileName(block.Text), Title = "Save attachment copy" });
        if (file?.TryGetLocalPath() is { } path) File.Copy(vm.Store.AttachmentPath(block.FileName), path, true);
    });
    private async Task EditDay(DayRule day)
    {
        if (day.Date.Date < DateTime.Today) return;
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(24) }; var dialog = new Window { Title = "Constraints · " + day.Label, Width = 420, Height = 620, Content = new ScrollViewer { Content = panel }, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        panel.Children.Add(Text(day.Label, DesignTokens.Title));
        var unavailable = new CheckBox { Content = "Unavailable", IsChecked = day.Unavailable }; panel.Children.Add(unavailable);
        NumericUpDown Number(string label, double value, decimal max)
        {
            panel.Children.Add(Label(label)); var input = new NumericUpDown { Minimum = 0, Maximum = max, Value = (decimal)value, Increment = .25m }; panel.Children.Add(input); return input;
        }
        var count = Number("Maximum tasks", day.MaxTasks, 100); count.Increment = 1;
        var start = Number("Working hours start (e.g. 9.5)", day.StartHour, 24); var end = Number("Working hours end", day.EndHour, 24); var hours = Number("Workload limit (hours)", day.MaxHours, 24);
        panel.Children.Add(Label("Allowed projects (none selected = all)")); var checks = new List<(Project p, CheckBox check)>();
        foreach (var p in vm.Data.Projects) { var check = new CheckBox { Content = p.Title, IsChecked = day.AllowedProjects.Contains(p.Id) }; checks.Add((p, check)); panel.Children.Add(check); }
        var error = Text("", 12, "Warning"); panel.Children.Add(error);
        panel.Children.Add(Button("Save constraints", () =>
        {
            if (new[] { count, start, end, hours }.Any(n => n.Value == null) || start.Value >= end.Value) { error.Text = "Enter valid values; working hours must end after they start."; return; }
            day.Unavailable = unavailable.IsChecked == true; day.MaxTasks = (int)count.Value!.Value; day.StartHour = (double)start.Value!.Value; day.EndHour = (double)end.Value!.Value; day.MaxHours = (double)hours.Value!.Value;
            day.AllowedProjects = checks.Where(c => c.check.IsChecked == true).Select(c => c.p.Id).ToList(); vm.Refresh(); dialog.Close();
        })); await dialog.ShowDialog(this);
    }
    private Task ConfirmDelete() { Guard(() => { vm.DeleteSelected(); taskEditorWindow?.Close(); }); return Task.CompletedTask; }
    private void DeleteProjectFromMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Project project }) Guard(() => vm.DeleteProject(project));
    }
    private void DeleteCurrentProject(object? sender, RoutedEventArgs e)
    {
        if (vm.SelectedProject is { } project) Guard(() => vm.DeleteProject(project));
    }
    private async void AddProject(object? sender, RoutedEventArgs e) => await CreateProject();
    private async Task CreateProject()
    {
        var project = await new NewProjectWindow(GraphColors.NextProject(vm.Data.Projects)).ShowDialog<Project?>(this);
        if (project == null) return;
        vm.Data.Projects.Add(project); vm.SelectedProject = project; vm.Refresh(); MainTabs.SelectedIndex = 0; ProjectTabs.SelectedIndex = 0;
    }
    private async Task ChooseProjectColor(Project project)
    {
        var color = await new ProjectColorWindow(project.DisplayColor).ShowDialog<string?>(this); if (color == null) return;
        project.ColorHex = color; vm.Refresh(); BuildInspector(); GraphView.RefreshTheme();
    }
    private async Task OpenTaskEditor(WorkTask task)
    {
        if (taskEditorWindow != null) { taskEditorWindow.Activate(); return; }
        vm.Selected = task;
        taskEditorPanel = new StackPanel { Spacing = 4, Margin = new Thickness(24) };
        var dialog = new Window { Title = "Task details", Width = 560, Height = 760, MinHeight = 500, Content = new ScrollViewer { Content = taskEditorPanel }, WindowStartupLocation = WindowStartupLocation.CenterOwner }; taskEditorWindow = dialog;
        BuildInspector();
        try { await dialog.ShowDialog(this); }
        finally { taskEditorPanel = null; taskEditorWindow = null; BuildInspector(); }
    }
    private void FocusTitle() { var title = InspectorContent.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(box => box.Name == "EntityTitle"); title?.Focus(); title?.SelectAll(); }
    private void AddNode(object? sender, RoutedEventArgs e) => Guard(() => { GraphView.CancelEndpoint(); MainTabs.SelectedIndex = 0; ProjectTabs.SelectedIndex = 1; vm.AddNode(); FocusTitle(); });
    private async void AddTask(object? sender, RoutedEventArgs e) { GraphView.CancelEndpoint(); MainTabs.SelectedIndex = 1; vm.AddTask(); if (vm.Selected is WorkTask task) await OpenTaskEditor(task); }
    private void ProjectSelected(object? sender, SelectionChangedEventArgs e) { if (vm != null && !vm.IsRestoringHistory && ProjectList.SelectedItem is Project p) { GraphView.CancelInteraction(); vm.SelectedProject = p; MainTabs.SelectedIndex = 0; } }
    private void OpenProjectInfo(object? sender, RoutedEventArgs e) => vm.Selected = vm.SelectedProject;
    private void OpenProjectGraph(object? sender, RoutedEventArgs e) { ProjectTabs.SelectedIndex = 1; GraphView.Focus(); }
    private void NavigateToGraph(GraphEdge edge) { taskEditorWindow?.Close(); vm.SelectedProject = vm.Data.Projects.Single(p => p.Id == edge.ProjectId); MainTabs.SelectedIndex = 0; ProjectTabs.SelectedIndex = 1; vm.Selected = edge; GraphView.Focus(); GraphView.Fit(); }
    private void WorkspaceKeyDown(object? sender, KeyEventArgs e)
    {
        var editingText = e.Source is TextBox || (e.Source as Visual)?.GetVisualAncestors().Any(v => v is TextBox) == true;
        if (!editingText && e.KeyModifiers == KeyModifiers.None && e.Key == Key.Delete) { _ = ConfirmDelete(); e.Handled = true; return; }
        if (!editingText && e.KeyModifiers == KeyModifiers.None && e.Key == Key.F2) { FocusTitle(); e.Handled = true; return; }
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (!editingText && e.Key == Key.Z) { if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) RedoEdit(null, e); else UndoEdit(null, e); }
        else if (!editingText && e.Key == Key.Y) RedoEdit(null, e);
        else if (!editingText && e.Key == Key.A && MainTabs.SelectedIndex == 0 && ProjectTabs.SelectedIndex == 1) vm.SelectGraph(vm.Nodes.Cast<Entity>().Concat(vm.Edges));
        else if (!editingText && e.Key == Key.D) Guard(() => { vm.DuplicateSelected(); ShowPastedSelection(); });
        else if (!editingText && e.Key is Key.C or Key.X or Key.V) _ = ClipboardEdit(e.Key);
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.P) { MainTabs.SelectedIndex = 0; ProjectSearch.Focus(); ProjectSearch.SelectAll(); }
        else if (e.Key == Key.K) { var search = MainTabs.SelectedIndex switch { 1 => TaskSearch, 2 => PlannerSearch, _ => ResearchSearch }; if (MainTabs.SelectedIndex == 0) ProjectTabs.SelectedIndex = 1; search.Focus(); search.SelectAll(); }
        else if (e.Key is Key.D1 or Key.D2 or Key.D3) MainTabs.SelectedIndex = e.Key == Key.D1 ? 0 : e.Key == Key.D2 ? 1 : 2;
        else if (e.Key == Key.G) { MainTabs.SelectedIndex = 0; ProjectTabs.SelectedIndex = 1; GraphView.Focus(); }
        else if (e.Key == Key.N) AddNode(null, e);
        else if (e.Key == Key.T) AddTask(null, e);
        else if (e.Key == Key.S) vm.Save();
        else if (e.Key == Key.OemComma) _ = OpenSettings();
        else if (e.Key == Key.OemQuestion) _ = OpenShortcuts();
        else return;
        e.Handled = true;
    }
    private void UndoEdit(object? sender, RoutedEventArgs e) { if (vm.GraphGestureActive) GraphView.CancelInteraction(); else vm.Undo(); }
    private void RedoEdit(object? sender, RoutedEventArgs e) { if (vm.GraphGestureActive) GraphView.CancelInteraction(); else vm.Redo(); }
    private async Task ClipboardEdit(Key key) => await GuardAsync(async () =>
    {
        if (Clipboard == null) throw new InvalidOperationException("The desktop clipboard is unavailable.");
        if (key == Key.V)
        {
            var text = await Clipboard.TryGetTextAsync() ?? ""; vm.Paste(text); ShowPastedSelection();
        }
        else { await Clipboard.SetTextAsync(vm.CopySelected()); if (key == Key.X) vm.DeleteSelected(); else vm.Status = "Copied selection"; }
    });
    private void ShowPastedSelection() { if (vm.Selected is WorkTask) MainTabs.SelectedIndex = 1; else { MainTabs.SelectedIndex = 0; ProjectTabs.SelectedIndex = 1; } GraphView.Focus(); }
    private void SelectGraphMode(object? sender, RoutedEventArgs e) { GraphView.Mode = GraphInteractionMode.Select; GraphView.Focus(); }
    private void MoveGraphMode(object? sender, RoutedEventArgs e) { GraphView.Mode = GraphInteractionMode.Move; GraphView.Focus(); }
    private async void ShowShortcuts(object? sender, RoutedEventArgs e) => await OpenShortcuts();
    private async Task OpenShortcuts()
    {
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = DesignTokens.Space2 };
        var dialog = new Window { Title = "Keyboard shortcuts", Width = 680, Height = 680, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = panel } };
        panel.Children.Add(Text("Workspace & graph shortcuts", DesignTokens.Title));
        foreach (var (keys, action) in new[] { ("Ctrl+scroll / Scroll", "Zoom graph / move canvas vertically"), ("Ctrl+Z", "Undo the last workspace edit"), ("Ctrl+Shift+Z / Ctrl+Y", "Redo"), ("Ctrl+C / Ctrl+X / Ctrl+V", "Copy / cut / paste selected items"), ("Ctrl+D / Ctrl+A", "Duplicate / select all graph items"), ("V / H", "Select mode / Move mode (graph focused)"), ("Shift-click / Ctrl-click", "Add or remove an item from the selection"), ("Ctrl+1 / Ctrl+2 / Ctrl+3", "Projects / Tasks / Plan"), ("Ctrl+G", "Open the current project's graph"), ("Ctrl+K", "Search the current research or task workspace"), ("Ctrl+Shift+P", "Search projects"), ("Ctrl+N / Ctrl+T", "Add state / add standalone task"), ("Ctrl+S", "Save"), ("Ctrl+,", "Settings"), ("F", "Fit graph (graph focused)"), ("F2 / Delete", "Rename / delete selected items"), ("Tab", "Choose start/end handle on selected arrow"), ("Arrow keys / Shift+arrows", "Move chosen end 1 / 10 graph units"), ("Enter / Alt+Enter", "Attach or place chosen end / leave it free"), ("Alt while dragging", "Bypass state snapping"), ("Esc", "Cancel the current graph gesture"), ("Ctrl+/ or ? in graph", "Show these shortcuts") })
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") }; row.Children.Add(Text(keys, DesignTokens.Body, "Accent")); var label = Text(action); Grid.SetColumn(label, 1); row.Children.Add(label); panel.Children.Add(row);
        }
        panel.Children.Add(Button("Close", () => dialog.Close())); await dialog.ShowDialog(this);
    }
    private void FitGraph(object? sender, RoutedEventArgs e) => GraphView.Fit();
    private void ZoomGraphIn(object? sender, RoutedEventArgs e) => ZoomGraph(1.2);
    private void ZoomGraphOut(object? sender, RoutedEventArgs e) => ZoomGraph(1 / 1.2);
    private void ZoomGraph(double factor)
    {
        var center = new Vector(GraphView.Bounds.Width / 2, GraphView.Bounds.Height / 2);
        var anchor = GraphView.ViewportLocation + center / GraphView.ViewportZoom;
        GraphView.ViewportZoom = Math.Clamp(GraphView.ViewportZoom * factor, GraphView.MinViewportZoom, GraphView.MaxViewportZoom);
        GraphView.ViewportLocation = anchor - center / GraphView.ViewportZoom;
        GraphView.Focus();
    }
    private void HideFinished(object? sender, RoutedEventArgs e) { if (vm == null) return; vm.HideCompleted = (sender as CheckBox)?.IsChecked == true; RenderLists(); }
    private async void AutoSchedule(object? sender, RoutedEventArgs e)
    {
        var counts = await new AutoAssignWindow(vm).ShowDialog<Dictionary<Guid, int>?>(this);
        if (counts != null) Guard(() => vm.AutoSchedule(counts));
    }
    private void PreviousWeek(object? sender, RoutedEventArgs e) => vm.NavigateCalendar(-1);
    private void NextWeek(object? sender, RoutedEventArgs e) => vm.NavigateCalendar(1);
    private void CalendarToday(object? sender, RoutedEventArgs e) => vm.ShowCalendarDate(DateTime.Today);
    private void ShowWeekView(object? sender, RoutedEventArgs e) { vm.MonthView = false; RenderLists(); }
    private void ShowMonthView(object? sender, RoutedEventArgs e) { vm.MonthView = true; RenderLists(); }
    private async void ShowGoogleCalendar(object? sender, RoutedEventArgs e) => await OpenGoogleCalendar();
    private async Task OpenGoogleCalendar(WorkTask? task = null)
    {
        var dialog = new GoogleCalendarWindow(vm, task); await dialog.ShowDialog(this); vm.ReloadCalendar();
    }
    private void ShowCalendarEvent(CalendarEvent entry)
    {
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        var dialog = new Window { Title = "Google Calendar", Width = 480, Height = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = panel } };
        panel.Children.Add(Text(entry.Title, DesignTokens.Title));
        panel.Children.Add(Text(entry.AllDay ? entry.Start.ToString("ddd, MMM d") + (entry.End > entry.Start.AddDays(1) ? " – " + entry.End.AddDays(-1).ToString("ddd, MMM d") : "") + " · All day" : entry.Start.ToString("ddd, MMM d HH:mm") + " – " + entry.End.ToString("ddd, MMM d HH:mm")));
        panel.Children.Add(Text(entry.Busy ? "Busy" : "Available", DesignTokens.Caption, "TextMuted")); panel.Children.Add(Text(entry.Description)); panel.Children.Add(Button("Close", () => dialog.Close())); _ = dialog.ShowDialog(this);
    }
    private async void ShowSettings(object? sender, RoutedEventArgs e) => await OpenSettings();
    private async Task OpenSettings()
    {
        GraphView.CancelEndpoint();
        var dialog = new SettingsWindow(appearance, preferences =>
        {
            var changedStartup = preferences.StartWithWindows != appearance.StartWithWindows;
            if (changedStartup) startupRegistration.SetEnabled(preferences.StartWithWindows);
            try { appearanceStore.Save(preferences); }
            catch { if (changedStartup) startupRegistration.SetEnabled(appearance.StartWithWindows); throw; }
            appearance = preferences; AppearanceManager.Apply(preferences);
            RenderLists(); BuildInspector(); GraphView.RefreshTheme(); vm.Status = "Settings saved";
        });
        await dialog.ShowDialog(this);
    }
    private void OpenStorage(object? sender, RoutedEventArgs e) => Guard(() => Process.Start(new ProcessStartInfo { FileName = vm.Store.Root, UseShellExecute = true }));
    private async void ExportBackup(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export complete offline backup", SuggestedFileName = "WorkTracker-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip", DefaultExtension = "zip" });
        if (file?.TryGetLocalPath() is { } path) { vm.Store.Export(vm.Data, path); vm.Status = "Backup exported to " + path; }
    });
    private async void ImportBackup(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Restore Work Tracker backup (.zip)", AllowMultiple = false });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        // Always preserve the current workspace before replacing it.
        var recovery = Path.Combine(vm.Store.Root, "before-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip"); vm.Store.Export(vm.Data, recovery);
        vm.Replace(vm.Store.Import(path)); BuildInspector(); GraphView.Fit(); vm.Status = "Backup restored. Previous workspace preserved at " + recovery;
    });
}
