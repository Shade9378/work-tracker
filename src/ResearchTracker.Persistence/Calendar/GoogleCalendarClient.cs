using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ResearchTracker.Core;

namespace ResearchTracker.Persistence.Calendar;

/// <summary>Optional, explicit network operations. No background traffic or dependency in the local workspace format.</summary>
public sealed class GoogleCalendarClient(GoogleCalendarStore store, HttpClient? http = null)
{
    private static readonly HttpClient DefaultHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient http = http ?? DefaultHttp;
    private readonly SemaphoreSlim gate = new(1, 1);
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string Scope = "https://www.googleapis.com/auth/calendar.events";
    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public async Task ConnectAsync(Func<Uri, Task>? openBrowser = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var connection = store.LoadConnection();
            if (connection.ClientId.Length == 0) throw new InvalidOperationException("Import Google Desktop app OAuth credentials first.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            var redirect = $"http://127.0.0.1:{port}/";
            using var listener = new HttpListener(); listener.Prefixes.Add(redirect); listener.Start();
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(48)); var state = Base64Url(RandomNumberGenerator.GetBytes(32));
            var uri = new Uri("https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", new Dictionary<string, string>
            {
                ["client_id"] = connection.ClientId, ["redirect_uri"] = redirect, ["response_type"] = "code", ["scope"] = Scope,
                ["access_type"] = "offline", ["prompt"] = "consent", ["state"] = state, ["code_challenge_method"] = "S256",
                ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            }.Select(p => Escape(p.Key) + "=" + Escape(p.Value))));
            if (openBrowser != null) await openBrowser(uri);
            else Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            var valid = context.Request.Url?.AbsolutePath == "/" && context.Request.QueryString["state"] == state;
            var code = valid ? context.Request.QueryString["code"] : null;
            var message = code != null ? "Work Tracker received your authorization. You can close this tab." : "Authorization was cancelled or could not be verified. Return to Work Tracker.";
            context.Response.StatusCode = valid ? 200 : 400; context.Response.ContentType = "text/plain; charset=utf-8";
            var responseBytes = Encoding.UTF8.GetBytes(message); context.Response.ContentLength64 = responseBytes.Length; context.Response.KeepAlive = false;
            await context.Response.OutputStream.WriteAsync(responseBytes, timeout.Token); context.Response.Close();
            if (!valid) throw new InvalidOperationException("Google sign-in could not be verified. Try connecting again.");
            if (code == null) throw new InvalidOperationException("Google Calendar access was not granted.");
            await TokenAsync(connection, new() { ["code"] = code, ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code" }, timeout.Token);
        }
        finally { gate.Release(); }
    }
    private async Task<GoogleConnection> TokenAsync(GoogleConnection connection, Dictionary<string, string> fields, CancellationToken ct)
    {
        fields["client_id"] = connection.ClientId;
        if (connection.ClientSecret.Length > 0) fields["client_secret"] = connection.ClientSecret;
        using var response = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(fields), ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Google authorization failed. Check your Desktop app credentials and consent-screen test users, then reconnect.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); var data = json.RootElement;
        if (data.TryGetProperty("scope", out var scope) && !(scope.GetString() ?? "").Split(' ').Contains(Scope)) throw new InvalidOperationException("Calendar event access was not granted. Reconnect and allow Calendar access.");
        var updated = connection with { AccessToken = data.GetProperty("access_token").GetString()!, RefreshToken = data.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString()! : connection.RefreshToken, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(data.GetProperty("expires_in").GetInt32()) };
        store.SaveConnection(updated); return updated;
    }
    private async Task<GoogleConnection> AuthorizedAsync(CancellationToken ct)
    {
        var connection = store.LoadConnection();
        if (!connection.Connected) throw new InvalidOperationException("Connect Google Calendar first.");
        if (connection.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return connection;
        if (connection.RefreshToken.Length == 0) throw new InvalidOperationException("Google authorization expired. Connect again.");
        return await TokenAsync(connection, new() { ["refresh_token"] = connection.RefreshToken, ["grant_type"] = "refresh_token" }, ct);
    }
    private async Task<HttpResponseMessage> SendAsync(GoogleConnection connection, HttpMethod method, string uri, object? body, CancellationToken ct, string? etag = null)
    {
        using var request = new HttpRequestMessage(method, uri); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.AccessToken);
        if (etag != null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        if (body != null) request.Content = JsonContent.Create(body);
        return await http.SendAsync(request, ct);
    }
    private static void Check(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Google access was revoked or expired. Connect again.",
            HttpStatusCode.Forbidden => "Google denied calendar access. Check Calendar API is enabled and that you can edit the selected calendar.",
            HttpStatusCode.NotFound => "Google calendar or event was not found. Check the calendar ID.",
            HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict => "The Google event changed during this operation. Refresh and send again.",
            _ => $"Google Calendar request failed ({(int)response.StatusCode}). Local tasks and cached events were retained."
        });
    }
    private static string EventsUrl(GoogleConnection connection) => "https://www.googleapis.com/calendar/v3/calendars/" + Escape(connection.CalendarId) + "/events";
    public async Task<CalendarCache> RefreshAsync(DateTime start, DateTime end, CancellationToken ct = default)
    {
        if (end <= start) throw new ArgumentException("Calendar range must end after it starts.");
        await gate.WaitAsync(ct);
        try
        {
            var connection = await AuthorizedAsync(ct); var events = new List<CalendarEvent>(); string? page = null;
            do
            {
                var url = EventsUrl(connection) + "?singleEvents=true&orderBy=startTime&maxResults=2500&timeMin=" + Escape(new DateTimeOffset(start).ToString("o")) + "&timeMax=" + Escape(new DateTimeOffset(end).ToString("o"));
                if (page != null) url += "&pageToken=" + Escape(page);
                using var response = await SendAsync(connection, HttpMethod.Get, url, null, ct); Check(response);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (document.RootElement.TryGetProperty("items", out var items))
                    foreach (var item in items.EnumerateArray()) { var parsed = ParseEvent(item, connection.SourceId); if (parsed != null) events.Add(parsed); }
                page = document.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            } while (!string.IsNullOrEmpty(page));
            // Only replace the cache after every page succeeded; remove deleted events within this fetched range.
            var previous = store.LoadCache();
            var merged = previous.Events.Where(e => !e.Overlaps(start, end)).Concat(events).GroupBy(e => e.Id).Select(g => g.Last()).ToList();
            var cache = new CalendarCache { RefreshedAt = DateTimeOffset.Now, RangeStart = start, RangeEnd = end, Events = merged };
            store.SaveCache(cache); return cache;
        }
        finally { gate.Release(); }
    }
    public static CalendarEvent? ParseEvent(JsonElement item, string sourceId)
    {
        string Value(string key) => item.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
        if (Value("status") == "cancelled" || !item.TryGetProperty("start", out var start) || !item.TryGetProperty("end", out var end)) return null;
        var allDay = start.TryGetProperty("date", out var date);
        DateTime ParseTime(JsonElement value) => DateTimeOffset.Parse(value.GetProperty("dateTime").GetString()!, CultureInfo.InvariantCulture).LocalDateTime;
        var from = allDay ? DateTime.ParseExact(date.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture) : ParseTime(start);
        var to = allDay ? DateTime.ParseExact(end.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture) : ParseTime(end);
        Guid? task = null;
        if (item.TryGetProperty("extendedProperties", out var properties) && properties.TryGetProperty("private", out var privateData)
            && privateData.TryGetProperty("workTrackerSource", out var source) && source.GetString() == sourceId
            && privateData.TryGetProperty("workTrackerTask", out var id) && Guid.TryParse(id.GetString(), out var parsed)) task = parsed;
        return new CalendarEvent { Id = Value("id"), Title = Value("summary") is { Length: > 0 } title ? title : "(Untitled event)", Description = Value("description"), Start = from, End = to, AllDay = allDay, Busy = Value("transparency") != "transparent", TaskId = task };
    }
    public async Task SendTaskAsync(WorkTask task, string projectTitle, CancellationToken ct = default)
    {
        if (!TaskSchedule.Occurrences(task).Any()) throw new InvalidOperationException("Schedule a task before sending it to Google.");
        foreach (var occurrence in TaskSchedule.Occurrences(task).ToArray()) await SendTaskOccurrenceAsync(task, projectTitle, occurrence.Start, ct);
    }
    public async Task SendTaskOccurrenceAsync(WorkTask task, string projectTitle, DateTime start, CancellationToken ct = default)
    {
        if (!TaskSchedule.Occurrences(task).Any(s => s.Start == start) || task.Status is WorkStatus.Cancelled || !double.IsFinite(task.EffortHours) || task.EffortHours <= 0 || task.EffortHours > 168) throw new InvalidOperationException("Schedule an active task before sending it to Google.");
        if (TimeZoneInfo.Local.IsInvalidTime(start) || TimeZoneInfo.Local.IsInvalidTime(start.AddHours(task.EffortHours))) throw new InvalidOperationException("This time does not exist because of daylight saving. Choose another time.");
        await gate.WaitAsync(ct);
        try
        {
            var connection = await AuthorizedAsync(ct);
            // Stable Google-compatible ID makes a retry idempotent even if the previous response was lost.
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connection.SourceId + task.Id.ToString("N") + (start == task.ScheduledStart ? "" : start.ToString("yyyyMMdd"))))).ToLowerInvariant();
            var url = EventsUrl(connection); string? etag = null; var exists = false;
            using (var existing = await SendAsync(connection, HttpMethod.Get, url + "/" + id, null, ct))
            {
                if (existing.StatusCode != HttpStatusCode.NotFound)
                {
                    Check(existing); using var json = JsonDocument.Parse(await existing.Content.ReadAsStringAsync(ct));
                    var parsed = ParseEvent(json.RootElement, connection.SourceId);
                    if (parsed?.TaskId != task.Id) throw new InvalidOperationException("This Google event is no longer linked to this task. It was left unchanged.");
                    etag = json.RootElement.TryGetProperty("etag", out var tag) ? tag.GetString() : null; exists = true;
                }
            }
            var payload = new Dictionary<string, object>
            {
                ["summary"] = task.Title, ["description"] = projectTitle + (task.Summary.Length > 0 ? "\n\n" + task.Summary : ""),
                ["start"] = new { dateTime = new DateTimeOffset(start).ToString("o") }, ["end"] = new { dateTime = new DateTimeOffset(start.AddHours(task.EffortHours)).ToString("o") },
                ["extendedProperties"] = new { @private = new { workTrackerSource = connection.SourceId, workTrackerTask = task.Id.ToString() } }
            };
            if (!exists) payload["id"] = id;
            using var result = await SendAsync(connection, exists ? HttpMethod.Patch : HttpMethod.Post, exists ? url + "/" + id + "?sendUpdates=none" : url + "?sendUpdates=none", payload, ct, etag); Check(result);
        }
        finally { gate.Release(); }
    }
}
