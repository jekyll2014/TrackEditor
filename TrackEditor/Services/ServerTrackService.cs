using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using TrackEditor.Core.Models;
using TrackEditor.Core.Services;
using TrackEditor.Models;

namespace TrackEditor.Services;

/// <summary>HTTP client for the TrackEditor server: bearer-token auth and track CRUD.
/// Mirrors the Blazor web client's AccountTrackService + TokenAuthStateProvider, using
/// AppSettings for token persistence instead of browser localStorage.</summary>
public class ServerTrackService
{
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    readonly AppSettings _settings;
    HttpClient? _http;

    record TokenResponse(string tokenType, string accessToken, long expiresIn, string refreshToken);

    public bool IsAuthenticated { get; private set; }
    public string? Email => _settings.ServerEmail;

    public ServerTrackService(AppSettings settings)
    {
        _settings = settings;
        if (string.IsNullOrEmpty(settings.ServerUrl)) return;
        InitClient(settings.ServerUrl);
        if (!string.IsNullOrEmpty(settings.ServerAccessToken) &&
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() < settings.ServerTokenExpires - 30)
        {
            ApplyToken(settings.ServerAccessToken);
            IsAuthenticated = true;
        }
        else if (!string.IsNullOrEmpty(settings.ServerRefreshToken))
        {
            // Expired access token but refresh token exists — lazy refresh on first API call.
            IsAuthenticated = true;
        }
    }

    void InitClient(string url)
    {
        _http?.Dispose();
        try { _http = new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + '/') }; }
        catch { _http = null; }
    }

    void ApplyToken(string token) =>
        _http!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    async Task<string?> ValidTokenAsync()
    {
        if (_http is null) return null;
        string? token = _settings.ServerAccessToken;
        if (string.IsNullOrEmpty(token)) return null;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < _settings.ServerTokenExpires - 30) return token;
        return await TryRefreshAsync();
    }

    async Task<string?> TryRefreshAsync()
    {
        string? rt = _settings.ServerRefreshToken;
        if (string.IsNullOrEmpty(rt) || _http is null) { ClearTokens(); return null; }
        try
        {
            var res = await _http.PostAsJsonAsync("api/auth/refresh", new { refreshToken = rt });
            if (!res.IsSuccessStatusCode) { ClearTokens(); return null; }
            var tr = await res.Content.ReadFromJsonAsync<TokenResponse>();
            if (tr is null) { ClearTokens(); return null; }
            StoreTokens(tr);
            ApplyToken(tr.accessToken);
            return tr.accessToken;
        }
        catch { ClearTokens(); return null; }
    }

    void StoreTokens(TokenResponse tr)
    {
        _settings.ServerAccessToken = tr.accessToken;
        _settings.ServerRefreshToken = tr.refreshToken;
        _settings.ServerTokenExpires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + tr.expiresIn;
        IsAuthenticated = true;
        _settings.Save();
    }

    void ClearTokens()
    {
        _settings.ServerAccessToken = null;
        _settings.ServerRefreshToken = null;
        _settings.ServerTokenExpires = 0;
        IsAuthenticated = false;
        _settings.Save();
    }

    async Task EnsureAuthAsync()
    {
        var token = await ValidTokenAsync();
        if (token is not null) ApplyToken(token);
    }

    // ── auth ─────────────────────────────────────────────────────────────────

    /// <summary>Returns null on success, or a human-readable error message.</summary>
    public async Task<string?> LoginAsync(string serverUrl, string email, string password)
    {
        _settings.ServerUrl = serverUrl.TrimEnd('/');
        _settings.ServerEmail = email;
        _settings.Save();
        InitClient(_settings.ServerUrl);
        if (_http is null) return "Invalid server URL.";
        try
        {
            var res = await _http.PostAsJsonAsync("api/auth/login", new { email, password });
            if (!res.IsSuccessStatusCode) return await ErrMsg(res, "Login failed — check email and password.");
            var tr = await res.Content.ReadFromJsonAsync<TokenResponse>();
            if (tr is null) return "Login failed (no token returned).";
            StoreTokens(tr);
            ApplyToken(tr.accessToken);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Registers then logs in. Returns null on success, or a human-readable error message.</summary>
    public async Task<string?> RegisterAsync(string serverUrl, string email, string password)
    {
        _settings.ServerUrl = serverUrl.TrimEnd('/');
        _settings.ServerEmail = email;
        _settings.Save();
        InitClient(_settings.ServerUrl);
        if (_http is null) return "Invalid server URL.";
        try
        {
            var res = await _http.PostAsJsonAsync("api/auth/register", new { email, password });
            if (!res.IsSuccessStatusCode) return await ErrMsg(res, "Registration failed.");
            return await LoginAsync(serverUrl, email, password);
        }
        catch (Exception ex) { return ex.Message; }
    }

    public void Logout()
    {
        ClearTokens();
        if (_http is not null) _http.DefaultRequestHeaders.Authorization = null;
    }

    // ── config ────────────────────────────────────────────────────────────────

    public async Task<AppConfigDto> GetConfigAsync()
    {
        if (_http is null) return new AppConfigDto(false, false);
        try { return await _http.GetFromJsonAsync<AppConfigDto>("api/config") ?? new(false, false); }
        catch { return new AppConfigDto(false, false); }
    }

    // ── tracks ────────────────────────────────────────────────────────────────

    public async Task<List<TrackSummaryDto>> ListAsync()
    {
        if (_http is null) return new();
        await EnsureAuthAsync();
        return await _http.GetFromJsonAsync<List<TrackSummaryDto>>("api/tracks") ?? new();
    }

    public async Task<StoredTrackDto?> GetAsync(Guid id)
    {
        if (_http is null) return null;
        await EnsureAuthAsync();
        return await _http.GetFromJsonAsync<StoredTrackDto>($"api/tracks/{id}");
    }

    public async Task<StoredTrackDto?> CreateAsync(string name, string trackJson)
    {
        if (_http is null) return null;
        await EnsureAuthAsync();
        var res = await _http.PostAsJsonAsync("api/tracks", new CreateTrackRequest(name, trackJson));
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<StoredTrackDto>();
    }

    public async Task UpdateAsync(Guid id, string name, string trackJson)
    {
        if (_http is null) return;
        await EnsureAuthAsync();
        (await _http.PutAsJsonAsync($"api/tracks/{id}", new UpdateTrackRequest(name, trackJson)))
            .EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(Guid id)
    {
        if (_http is null) return;
        await EnsureAuthAsync();
        (await _http.DeleteAsync($"api/tracks/{id}")).EnsureSuccessStatusCode();
    }

    public async Task<TrackSummaryDto?> ShareAsync(Guid id)
    {
        if (_http is null) return null;
        await EnsureAuthAsync();
        var res = await _http.PostAsync($"api/tracks/{id}/share", null);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<TrackSummaryDto>();
    }

    public async Task<TrackSummaryDto?> UnshareAsync(Guid id)
    {
        if (_http is null) return null;
        await EnsureAuthAsync();
        var res = await _http.PostAsync($"api/tracks/{id}/unshare", null);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<TrackSummaryDto>();
    }

    // ── public sharing (anonymous) ────────────────────────────────────────────

    public async Task<StoredTrackDto?> GetSharedAsync(Guid id)
    {
        if (_http is null) return null;
        var res = await _http.GetAsync($"api/shared/{id}");
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<StoredTrackDto>();
    }

    // ── serialization ─────────────────────────────────────────────────────────

    public string TrackToJson(Track track) => JsonSerializer.Serialize(track, JsonOpts);

    public Track? JsonToTrack(string json)
    {
        try { return JsonSerializer.Deserialize<Track>(json, JsonOpts); }
        catch { return null; }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    static async Task<string> ErrMsg(HttpResponseMessage res, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
                return d.GetString()!;
            if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Object)
                foreach (var p in errs.EnumerateObject())
                    foreach (var m in p.Value.EnumerateArray())
                    {
                        var s = m.GetString();
                        if (s is not null) return s;
                    }
        }
        catch { }
        return fallback;
    }
}
