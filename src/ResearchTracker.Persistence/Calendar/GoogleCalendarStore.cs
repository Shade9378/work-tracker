using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ResearchTracker.Core;

namespace ResearchTracker.Persistence.Calendar;

public sealed record GoogleConnection
{
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public string CalendarId { get; init; } = "primary";
    public string SourceId { get; init; } = Guid.NewGuid().ToString("N");
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
    public bool Connected => RefreshToken.Length > 0 || AccessToken.Length > 0;
}

public sealed record CalendarCache
{
    public DateTimeOffset? RefreshedAt { get; init; }
    public DateTime RangeStart { get; init; }
    public DateTime RangeEnd { get; init; }
    public List<CalendarEvent> Events { get; init; } = [];
}

public interface ICredentialProtection
{
    byte[] Protect(byte[] bytes);
    byte[] Unprotect(byte[] bytes);
}

/// <summary>Windows user-bound DPAPI, with no plaintext fallback.</summary>
public sealed class WindowsCredentialProtection : ICredentialProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public byte[] Protect(byte[] bytes) => Transform(bytes, true);
    public byte[] Unprotect(byte[] bytes) => Transform(bytes, false);
    private static byte[] Transform(byte[] bytes, bool encrypt)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Google credentials require Windows user-bound encryption on this build.");
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) }; Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = encrypt ? CryptProtectData(ref input, "Work Tracker Google Calendar", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not access encrypted Google credentials for this Windows user.");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
}

public sealed class GoogleCalendarStore(string root, ICredentialProtection? protection = null)
{
    private readonly ICredentialProtection protection = protection ?? new WindowsCredentialProtection();
    private string ConnectionPath => Path.Combine(root, "google-calendar.credentials");
    private string CachePath => Path.Combine(root, "google-calendar.cache.json");
    private string IdentityPath => Path.Combine(root, "google-calendar.identity");
    public GoogleConnection LoadConnection() => File.Exists(ConnectionPath)
        ? JsonSerializer.Deserialize<GoogleConnection>(protection.Unprotect(File.ReadAllBytes(ConnectionPath))) ?? new() : new();
    public CalendarCache LoadCache()
    {
        try { return File.Exists(CachePath) ? JsonSerializer.Deserialize<CalendarCache>(File.ReadAllText(CachePath)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }
    public void SaveConnection(GoogleConnection connection) => AtomicWrite(ConnectionPath, protection.Protect(JsonSerializer.SerializeToUtf8Bytes(connection)));
    public void SaveCache(CalendarCache cache) => AtomicWrite(CachePath, JsonSerializer.SerializeToUtf8Bytes(cache));
    public void Disconnect() { File.Delete(ConnectionPath); File.Delete(CachePath); }
    private static void AtomicWrite(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, data); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public GoogleConnection ImportCredentials(string json, string calendarId)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("installed", out var installed)) throw new InvalidDataException("Choose Google OAuth credentials of type Desktop app (the JSON must contain ‘installed’).");
        var id = installed.GetProperty("client_id").GetString() ?? "";
        if (!id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(calendarId)) throw new InvalidDataException("Invalid desktop client ID or calendar ID.");
        var identity = File.Exists(IdentityPath) && Guid.TryParse(File.ReadAllText(IdentityPath), out var saved) ? saved.ToString("N") : Guid.NewGuid().ToString("N");
        AtomicWrite(IdentityPath, Encoding.UTF8.GetBytes(identity));
        var result = new GoogleConnection { ClientId = id, ClientSecret = installed.TryGetProperty("client_secret", out var secret) ? secret.GetString() ?? "" : "", CalendarId = calendarId.Trim(), SourceId = identity };
        SaveConnection(result); SaveCache(new()); return result;
    }
}
