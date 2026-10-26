using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ResearchTracker.Application;
using ResearchTracker.Core;
using ResearchTracker.Desktop;
using ResearchTracker.Persistence;
using ResearchTracker.Persistence.Calendar;
using Xunit;

namespace ResearchTracker.Tests;

public sealed class CalendarTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WorkTrackerCalendar-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class TestProtection : ICredentialProtection
    {
        public byte[] Protect(byte[] bytes) => bytes.Select(b => (byte)(b ^ 127)).ToArray();
        public byte[] Unprotect(byte[] bytes) => Protect(bytes);
    }
    private GoogleCalendarStore Store => new(root, new TestProtection());
    private GoogleConnection Connected(bool expired = false)
    {
        var connection = new GoogleConnection { ClientId = "test.apps.googleusercontent.com", ClientSecret = "secret", AccessToken = "access", RefreshToken = "refresh", ExpiresAt = expired ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow.AddHours(1), SourceId = "source", CalendarId = "calendar@example.com" };
        Store.SaveConnection(connection); return connection;
    }
    private sealed class FakeHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request);
    }
    private static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static string EventJson(string id = "event", string title = "Meeting") => JsonSerializer.Serialize(new { id, summary = title, start = new { dateTime = "2030-01-07T09:00:00Z" }, end = new { dateTime = "2030-01-07T10:00:00Z" } });

    [Fact] public void MonthGridIncludesLeapDayAndYearBoundaryWithMondayStart()
    {
        var cells = CalendarDates.MonthCells(new DateTime(2028, 2, 15)); Assert.Equal(42, cells.Count); Assert.Equal(DayOfWeek.Monday, cells[0].DayOfWeek); Assert.Contains(new DateTime(2028, 2, 29), cells);
        Assert.Equal(new DateTime(2029, 12, 31), CalendarDates.Monday(new DateTime(2030, 1, 1)));
        Assert.All(cells.Zip(cells.Skip(1)), pair => Assert.Equal(TimeSpan.FromDays(1), pair.Second - pair.First));
    }
    [Fact] public void CredentialsAreEncryptedAndIdentitySurvivesDisconnectAndImport()
    {
        const string json = "{\"installed\":{\"client_id\":\"test.apps.googleusercontent.com\",\"client_secret\":\"private-value\"}}";
        var first = Store.ImportCredentials(json, "primary"); Assert.Equal(first, Store.LoadConnection());
        Assert.DoesNotContain("private-value", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "google-calendar.credentials"))));
        Store.SaveCache(new() { Events = [new() { Id = "cached" }] }); Store.Disconnect(); Assert.False(Store.LoadConnection().Connected); Assert.Empty(Store.LoadCache().Events);
        Assert.Equal(first.SourceId, Store.ImportCredentials(json, "primary").SourceId);
        Assert.Throws<InvalidDataException>(() => Store.ImportCredentials("{\"web\":{}}", "primary"));
    }
    [Fact] public void WindowsProtectionRoundTripsAndDoesNotStorePlaintext()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protector = new WindowsCredentialProtection(); var raw = Encoding.UTF8.GetBytes("calendar-refresh-secret"); var encrypted = protector.Protect(raw);
        Assert.Equal(raw, protector.Unprotect(encrypted)); Assert.DoesNotContain("calendar-refresh-secret", Encoding.UTF8.GetString(encrypted));
    }
    [Fact] public void BusyEventsBlockSchedulerAndCliWhileTransparentAndOwnEventsDoNot()
    {
        var day = new DateTime(2030, 1, 7); var project = new Project { Title = "P" }; var task = new WorkTask { ProjectId = project.Id, Title = "T" };
        var workspace = new Workspace { Projects = [project], Tasks = [task], Days = [new() { Date = day }] };
        var entry = new CalendarEvent { Id = "busy", Start = day.AddHours(9), End = day.AddHours(10), Busy = true };
        var scheduler = new Scheduler { CalendarEvents = [entry] }; Assert.Contains("Overlaps a Google Calendar event.", scheduler.Conflicts(workspace, task, day.AddHours(9)));
        Assert.Empty(scheduler.Schedule(workspace, day)); Assert.Equal(day.AddHours(10), task.ScheduledStart);
        scheduler.CalendarEvents = [entry with { Busy = false }]; Assert.Empty(scheduler.Conflicts(workspace, task, day.AddHours(9)));
        scheduler.CalendarEvents = [entry with { TaskId = task.Id }]; Assert.Empty(scheduler.Conflicts(workspace, task, day.AddHours(9)));
        new WorkspaceStore(root).Save(workspace); Store.SaveCache(new() { Events = [entry] });
        Assert.Throws<InvalidOperationException>(() => new WorkspaceCommands(root).tasks_schedule(task.Id, "2030-01-07 09:00"));
    }
    [Fact] public void ParsesAllDayExclusiveEndOffsetsTransparentCancelledAndOwnership()
    {
        var taskId = Guid.NewGuid();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { id = "day", summary = "Holiday", transparency = "transparent", start = new { date = "2030-01-07" }, end = new { date = "2030-01-09" }, extendedProperties = new { @private = new { workTrackerSource = "source", workTrackerTask = taskId } } }));
        var entry = GoogleCalendarClient.ParseEvent(doc.RootElement, "source")!;
        Assert.True(entry.AllDay); Assert.False(entry.Busy); Assert.Equal(taskId, entry.TaskId); Assert.True(entry.Overlaps(new DateTime(2030, 1, 8), new DateTime(2030, 1, 9))); Assert.False(entry.Overlaps(new DateTime(2030, 1, 9), new DateTime(2030, 1, 10)));
        Assert.Null(GoogleCalendarClient.ParseEvent(doc.RootElement, "other")!.TaskId);
        using var cancelled = JsonDocument.Parse("{\"status\":\"cancelled\"}"); Assert.Null(GoogleCalendarClient.ParseEvent(cancelled.RootElement, "source"));
        using var timed = JsonDocument.Parse(EventJson()); Assert.Equal(DateTimeOffset.Parse("2030-01-07T09:00:00Z").LocalDateTime, GoogleCalendarClient.ParseEvent(timed.RootElement, "source")!.Start);
    }
    [Fact] public async Task RefreshHandlesPagesTokenRenewalAndReplacesDeletedEventsOnlyInRange()
    {
        Connected(true); var start = new DateTime(2030, 1, 7); Store.SaveCache(new() { Events = [new() { Id = "deleted", Start = start, End = start.AddDays(1) }, new() { Id = "outside", Start = start.AddMonths(-1), End = start.AddMonths(-1).AddHours(1) }] });
        var calls = 0;
        using var http = new HttpClient(new FakeHttp(async request =>
        {
            calls++;
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                var form = await request.Content!.ReadAsStringAsync(); Assert.Contains("grant_type=refresh_token", form); return Response("{\"access_token\":\"renewed\",\"expires_in\":3600}");
            }
            Assert.Equal("renewed", request.Headers.Authorization!.Parameter); Assert.Contains("calendar%40example.com", request.RequestUri.AbsoluteUri);
            return request.RequestUri.Query.Contains("pageToken=next") ? Response("{\"items\":[" + EventJson("second") + "]}") : Response("{\"nextPageToken\":\"next\",\"items\":[" + EventJson() + "]}");
        }));
        var cache = await new GoogleCalendarClient(Store, http).RefreshAsync(start, start.AddDays(7), TestContext.Current.CancellationToken);
        Assert.Equal(3, calls); Assert.Equal(3, cache.Events.Count); Assert.DoesNotContain(cache.Events, e => e.Id == "deleted"); Assert.Contains(cache.Events, e => e.Id == "outside"); Assert.Equal("refresh", Store.LoadConnection().RefreshToken);
    }
    [Fact] public async Task FailedRefreshPreservesWholePreviousCache()
    {
        Connected(); var start = new DateTime(2030, 1, 7); Store.SaveCache(new() { Events = [new() { Id = "retained", Start = start, End = start.AddDays(1) }] });
        using var http = new HttpClient(new FakeHttp(request => Task.FromResult(request.RequestUri!.Query.Contains("pageToken") ? Response("{}", HttpStatusCode.Forbidden) : Response("{\"nextPageToken\":\"next\",\"items\":[" + EventJson() + "]}"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GoogleCalendarClient(Store, http).RefreshAsync(start, start.AddDays(7), TestContext.Current.CancellationToken)); Assert.Equal("retained", Assert.Single(Store.LoadCache().Events).Id);
    }
    [Fact] public async Task SendingTaskUsesStableIdUpdatesOnlyOwnedEventAndHonorsEtag()
    {
        Connected(); var task = new WorkTask { Title = "Research", Summary = "Summary", ScheduledStart = new DateTime(2030, 1, 7, 9, 0, 0) }; string? saved = null; string? id = null; var writes = 0;
        using var http = new HttpClient(new FakeHttp(async request =>
        {
            if (request.Method == HttpMethod.Get) return saved == null ? Response("{}", HttpStatusCode.NotFound) : Response(saved);
            var text = await request.Content!.ReadAsStringAsync(); using var payload = JsonDocument.Parse(text); writes++;
            Assert.Equal("Research", payload.RootElement.GetProperty("summary").GetString()); Assert.Contains("Project", payload.RootElement.GetProperty("description").GetString());
            if (saved == null) { Assert.Equal(HttpMethod.Post, request.Method); id = payload.RootElement.GetProperty("id").GetString(); Assert.Matches("^[0-9a-f]{64}$", id!); }
            else { Assert.Equal(HttpMethod.Patch, request.Method); Assert.Contains(id!, request.RequestUri!.AbsoluteUri); Assert.Equal("\"etag\"", request.Headers.GetValues("If-Match").Single()); }
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text)!;
            parsed["id"] = JsonSerializer.SerializeToElement(id); parsed["etag"] = JsonSerializer.SerializeToElement("\"etag\""); saved = JsonSerializer.Serialize(parsed); return Response(saved);
        }));
        var client = new GoogleCalendarClient(Store, http); await client.SendTaskAsync(task, "Project", TestContext.Current.CancellationToken); task.Summary = "Updated"; await client.SendTaskAsync(task, "Project", TestContext.Current.CancellationToken); Assert.Equal(2, writes);
        saved = EventJson(id!); await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendTaskAsync(task, "Project", TestContext.Current.CancellationToken)); Assert.Equal(2, writes);
    }
    [Fact] public async Task RepeatedPlacementsSendSeparateGoogleEvents()
    {
        Connected(); var task = new WorkTask { Title = "Repeat", ScheduledStart = new DateTime(2030, 1, 7, 9, 0, 0) }; TaskSchedule.Set(task, new DateTime(2030, 1, 8, 10, 0, 0), true);
        var ids = new HashSet<string>(); var starts = new List<DateTimeOffset>();
        using var http = new HttpClient(new FakeHttp(async request =>
        {
            if (request.Method == HttpMethod.Get) return Response("{}", HttpStatusCode.NotFound);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.True(ids.Add(payload.RootElement.GetProperty("id").GetString()!)); starts.Add(payload.RootElement.GetProperty("start").GetProperty("dateTime").GetDateTimeOffset()); return Response("{}");
        }));
        await new GoogleCalendarClient(Store, http).SendTaskAsync(task, "Project", TestContext.Current.CancellationToken); Assert.Equal(2, ids.Count); Assert.Equal(new[] { 7, 8 }, starts.Select(s => s.Day));
    }
    [Fact] public async Task OAuthUsesLoopbackPkceAndPersistsOnlyAfterVerifiedState()
    {
        Store.ImportCredentials("{\"installed\":{\"client_id\":\"test.apps.googleusercontent.com\",\"client_secret\":\"secret\"}}", "primary");
        string? challenge = null; Task<HttpResponseMessage>? callback = null; var posts = 0;
        using var loopback = new HttpClient();
        using var http = new HttpClient(new FakeHttp(async request =>
        {
            posts++; var form = (await request.Content!.ReadAsStringAsync()).Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]));
            var hash = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_'); Assert.Equal(challenge, hash);
            Assert.Equal("authorization_code", form["grant_type"]); Assert.Equal("received-code", form["code"]);
            return Response("{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}");
        }));
        var client = new GoogleCalendarClient(Store, http);
        Task Open(Uri uri, bool valid)
        {
            var args = uri.Query[1..].Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]));
            Assert.Equal("S256", args["code_challenge_method"]); challenge = args["code_challenge"];
            callback = loopback.GetAsync(args["redirect_uri"] + "?code=received-code&state=" + (valid ? args["state"] : "wrong")); return Task.CompletedTask;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(uri => Open(uri, false), TestContext.Current.CancellationToken)); using (await callback!) { } Assert.Equal(0, posts); Assert.False(Store.LoadConnection().Connected);
        await client.ConnectAsync(uri => Open(uri, true), TestContext.Current.CancellationToken); using (await callback!) { } Assert.Equal(1, posts); Assert.True(Store.LoadConnection().Connected);
    }
    [AvaloniaFact] public void NativeMonthViewNavigatesSelectsTasksAndShowsGoogleCache()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            window.FindControl<TabControl>("MainTabs")!.SelectedIndex = 2; vm.ShowCalendarDate(new DateTime(2030, 1, 7));
            window.FindControl<Button>("MonthViewButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Assert.True(vm.MonthView); Assert.False(window.FindControl<ScrollViewer>("WeekScroll")!.IsVisible); Assert.Equal("January 2030", window.FindControl<TextBlock>("WeekLabel")!.Text);
            var view = Assert.IsType<MonthCalendarView>(window.FindControl<ContentControl>("MonthHost")!.Content); Assert.Equal(42, view.GetVisualDescendants().OfType<Border>().Count(b => b.Name?.StartsWith("CalendarDay") == true));
            var task = vm.Data.Tasks.First(); vm.ManualSchedule(task, new DateTime(2030, 1, 7, 10, 0, 0));
            vm.CalendarStore.SaveCache(new() { RefreshedAt = DateTimeOffset.Now, RangeStart = vm.WeekStart, RangeEnd = vm.WeekStart.AddDays(7), Events = [new() { Id = "meeting", Title = "Lab meeting", Start = new DateTime(2030, 1, 7, 11, 0, 0), End = new DateTime(2030, 1, 7, 12, 0, 0) }] }); vm.ReloadCalendar(); Dispatcher.UIThread.RunJobs();
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Lab meeting") == true);
            var output = Environment.GetEnvironmentVariable("WORK_TRACKER_SCREENSHOTS");
            if (output != null) { Directory.CreateDirectory(output); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, "calendar-month.png")); }
            void ChangeTheme(string theme)
            {
                window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
                var preferences = Assert.Single(window.OwnedWindows); preferences.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ThemeChoice").SelectedItem = theme;
                preferences.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SaveSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            }
            ChangeTheme("Light");
            if (output != null) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, "calendar-month-light.png")); }
            ChangeTheme("Dark");
            view.GetVisualDescendants().OfType<Button>().First(b => b.Content is TextBlock text && text.Text?.Contains("Lab meeting") == true).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            var details = Assert.Single(window.OwnedWindows); Assert.Equal("Google Calendar", details.Title); details.Close();
            window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Google Calendar…")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            var setup = Assert.IsType<GoogleCalendarWindow>(Assert.Single(window.OwnedWindows)); Assert.Contains(setup.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Not connected") == true);
            if (output != null) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); using var frame = setup.CaptureRenderedFrame(); frame!.Save(Path.Combine(output, "google-calendar.png")); }
            setup.Close(); Dispatcher.UIThread.RunJobs();
            view.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "7")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.False(vm.MonthView); Assert.Equal(new DateTime(2030, 1, 7), vm.WeekStart);
            vm.MonthView = true; vm.ShowCalendarDate(new DateTime(2030, 12, 1)); vm.NavigateCalendar(1); Assert.Equal(new DateTime(2031, 1, 1), vm.CalendarDate);
        }
        finally { window.Close(); }
    }
}
