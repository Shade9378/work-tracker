using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ResearchTracker.Core;

namespace ResearchTracker.Desktop;

public sealed class GoogleCalendarWindow : Window
{
    private readonly MainViewModel vm;
    private readonly CancellationTokenSource cancellation = new();
    private readonly TextBlock status = new() { Name = "GoogleStatus", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock connectionLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock cacheLabel = new() { TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private readonly TextBox calendarId = new() { Name = "GoogleCalendarId", Text = "primary", PlaceholderText = "primary" };
    private readonly List<Button> actions = [];
    private readonly ProgressBar progress = new() { IsIndeterminate = true, IsVisible = false, Height = 3 };
    private bool busy;
    private bool closed;
    public GoogleCalendarWindow(MainViewModel model, WorkTask? selectedTask = null)
    {
        vm = model; Title = "Google Calendar"; Width = 560; Height = 620; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(24, 24, 24, 8) };
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") }; layout.Children.Add(new ScrollViewer { Content = panel }); Content = layout;
        panel.Children.Add(new TextBlock { Text = "Google Calendar", FontSize = DesignTokens.Title, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(connectionLabel);
        panel.Children.Add(new TextBlock { Text = "Calendar ID", Classes = { "label" } }); panel.Children.Add(calendarId);
        panel.Children.Add(new TextBlock { Text = "Use primary for your main calendar, or its ID from Google Calendar settings. Import Desktop app OAuth credentials to choose a calendar.", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
        var credentials = Action("Import credentials…", ImportCredentials); credentials.Name = "ImportGoogleCredentials"; panel.Children.Add(credentials);
        var setup = new StackPanel { Spacing = 8 };
        setup.Children.Add(new TextBlock { Text = "In Google Cloud Console, enable Google Calendar API, configure the consent screen (add your account as a test user if in Testing), then create an OAuth client of type Desktop app and download its JSON.", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
        var console = new Button { Content = "Open Google Cloud Console…" };
        console.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://console.cloud.google.com/apis/credentials") { UseShellExecute = true }); } catch (Exception ex) { status.Text = ex.Message; } };
        setup.Children.Add(console); panel.Children.Add(new Expander { Header = "Get OAuth credentials", Content = setup, HorizontalAlignment = HorizontalAlignment.Stretch });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Action("Connect…", async () => { await vm.GoogleCalendar.ConnectAsync(cancellationToken: cancellation.Token); status.Text = "Connected. Refresh to show Google events."; }));
        row.Children.Add(Action("Disconnect", () => { vm.CalendarStore.Disconnect(); vm.ReloadCalendar(); status.Text = "Disconnected. Local tasks and events already sent to Google were kept."; return Task.CompletedTask; })); panel.Children.Add(row);
        panel.Children.Add(new Separator());
        panel.Children.Add(cacheLabel);
        panel.Children.Add(Action("Refresh visible dates", async () => { await vm.RefreshCalendarAsync(cancellation.Token); status.Text = "Google events refreshed."; }));
        var tasks = (selectedTask == null ? vm.Data.Tasks.Where(t => t.Status is WorkStatus.Todo or WorkStatus.InProgress) : new[] { selectedTask }).SelectMany(t => TaskSchedule.Occurrences(t).Where(s => selectedTask != null || s.Start >= vm.WeekStart && s.Start < vm.WeekStart.AddDays(7)).Select(s => (Task: t, Start: s.Start))).ToList();
        panel.Children.Add(new Separator());
        panel.Children.Add(new TextBlock { Text = selectedTask == null ? "Send this week's scheduled tasks" : "Send selected task", FontSize = DesignTokens.Section });
        panel.Children.Add(new TextBlock { Text = selectedTask == null ? vm.WeekStart.ToString("MMM d") + " – " + vm.WeekStart.AddDays(6).ToString("MMM d, yyyy") : selectedTask.Title, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
        var preview = new StackPanel { Spacing = 4 };
        foreach (var task in tasks.OrderBy(t => t.Start)) preview.Children.Add(new TextBlock { Text = task.Start.ToString("ddd HH:mm") + "  " + task.Task.Title, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new ScrollViewer { Content = preview, MaxHeight = 120 });
        var send = Action("Send " + tasks.Count + (tasks.Count == 1 ? " task" : " tasks"), async () =>
        {
            var sent = 0;
            foreach (var task in tasks)
            {
                try
                {
                    await vm.GoogleCalendar.SendTaskOccurrenceAsync(task.Task, vm.Data.Projects.FirstOrDefault(p => p.Id == task.Task.ProjectId)?.Title ?? "", task.Start, cancellation.Token); sent++;
                    status.Text = $"Sent {sent}/{tasks.Count}.";
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { throw new InvalidOperationException($"Sent {sent}/{tasks.Count}. {task.Task.Title}: {ex.Message}", ex); }
            }
            try { await vm.RefreshCalendarAsync(cancellation.Token); status.Text = $"Sent {sent} task(s) and refreshed events."; }
            catch (Exception ex) when (ex is not OperationCanceledException) { status.Text = $"Sent {sent} task(s). Refresh failed: {ex.Message}"; }
        });
        send.Name = "SendGoogleTasks"; send.IsVisible = tasks.Count > 0; panel.Children.Add(send);
        panel.Children.Add(new TextBlock { Text = "Sends task titles, summaries, project names and scheduled times. Re-sending updates linked events. Google edits stay in Google; task changes are sent only when you click Send. Local deletion does not delete Google events.", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(24, 8, 24, 20) }; Grid.SetRow(footer, 1); layout.Children.Add(footer);
        footer.Children.Add(progress); footer.Children.Add(status);
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right }; close.Click += (_, _) => Close(); footer.Children.Add(close);
        Closed += (_, _) => { closed = true; cancellation.Cancel(); }; UpdateLabels();
    }
    private Button Action(string title, Func<Task> action)
    {
        var button = new Button { Content = title }; actions.Add(button);
        button.Click += async (_, _) =>
        {
            if (busy) return; busy = true; progress.IsVisible = true; foreach (var item in actions) item.IsEnabled = false; calendarId.IsEnabled = false;
            try { status.Text = "Working…"; await action(); }
            catch (OperationCanceledException) { status.Text = "Cancelled or timed out. Local data was retained."; }
            catch (Exception e) { status.Text = e.Message; }
            finally { busy = false; if (!closed) { progress.IsVisible = false; foreach (var item in actions) item.IsEnabled = true; calendarId.IsEnabled = true; UpdateLabels(); } }
        };
        return button;
    }
    private void UpdateLabels()
    {
        try
        {
            var connection = vm.CalendarStore.LoadConnection(); calendarId.Text = connection.CalendarId;
            connectionLabel.Text = connection.Connected ? "Connected · " + connection.CalendarId : connection.ClientId.Length > 0 ? "Credentials imported · ready to connect" : "Not connected · local planning works offline";
        }
        catch (Exception ex) { connectionLabel.Text = "Cannot read credentials. Import them again. " + ex.Message; }
        var cache = vm.CalendarStore.LoadCache();
        cacheLabel.Text = cache.RefreshedAt == null ? "No cached events. Refresh after connecting." : "Last refresh: " + cache.RefreshedAt.Value.LocalDateTime.ToString("MMM d, HH:mm") + " · " + cache.RangeStart.ToString("MMM d") + " – " + cache.RangeEnd.AddDays(-1).ToString("MMM d") + ". Cached events remain available offline.";
    }
    private async Task ImportCredentials()
    {
        var id = calendarId.Text?.Trim() ?? "";
        if (id.Length == 0) throw new InvalidOperationException("Enter a calendar ID, or primary.");
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import Google Desktop app OAuth JSON", AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType("OAuth JSON") { Patterns = new[] { "*.json" } } } });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) { status.Text = "Import cancelled."; return; }
        vm.CalendarStore.ImportCredentials(await File.ReadAllTextAsync(path, cancellation.Token), id); vm.ReloadCalendar(); status.Text = "Credentials imported. Click Connect to sign in with Google.";
    }
}
