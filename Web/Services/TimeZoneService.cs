using Microsoft.JSInterop;

namespace PeripheralMonitor.Web.Services;

/// <summary>
/// Reads the browser's local timezone offset once via JS interop and converts
/// UTC DateTimes to the user's local time. Scoped (one per browser session).
/// Solves the Blazor Server problem where ToLocalTime() uses the server's
/// timezone (UTC in Docker) instead of the browser's timezone.
/// </summary>
public class TimeZoneService
{
    private readonly IJSRuntime _js;
    private TimeSpan? _offset;

    public TimeZoneService(IJSRuntime js) => _js = js;

    public async Task<TimeSpan> GetOffsetAsync()
    {
        if (_offset.HasValue) return _offset.Value;
        try
        {
            var minutes = await _js.InvokeAsync<int>("pmGetTimezoneOffsetMinutes");
            _offset = TimeSpan.FromMinutes(minutes);
        }
        catch
        {
            _offset = TimeSpan.Zero; // fallback to UTC if JS not yet available
        }
        return _offset.Value;
    }

    public async Task<DateTime> ToLocalAsync(DateTime utc)
    {
        var offset = await GetOffsetAsync();
        return utc.Kind == DateTimeKind.Utc
            ? utc.Add(offset)
            : utc.ToUniversalTime().Add(offset);
    }

    public async Task<string> FormatAsync(DateTime? utc, string format = "yyyy-MM-dd HH:mm:ss")
    {
        if (utc == null) return "—";
        var local = await ToLocalAsync(utc.Value);
        return local.ToString(format);
    }
}
