using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using LinkCloudPlayer.Models;

namespace LinkCloudPlayer.Services;

// Thin wrapper around the link-cloud backend's HTTP API. Cookie-based auth
// (see backend/src/middleware/auth.js) is handled by HttpClientHandler's own
// CookieContainer, which - unlike a browser - we can also read directly, so
// GetAuthToken() can hand the raw JWT to things that aren't HttpClient (e.g.
// the LibVLC media URL, which can't share this process's cookie jar).
public class ApiClient
{
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    public Uri BaseUri { get; }

    public ApiClient(string baseUrl)
    {
        BaseUri = new Uri(baseUrl);
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = _cookies,
        };
        _http = new HttpClient(handler) { BaseAddress = BaseUri };
    }

    public async Task<User> LoginAsync(string email, string password)
    {
        var res = await _http.PostAsJsonAsync("/api/auth/login", new { email, password });
        await EnsureSuccess(res);
        var body = await res.Content.ReadFromJsonAsync<UserEnvelope>()
            ?? throw new ApiException("Empty response from server");
        return body.User;
    }

    public async Task<User?> MeAsync()
    {
        var res = await _http.GetAsync("/api/auth/me");
        if (res.StatusCode == HttpStatusCode.Unauthorized) return null;
        await EnsureSuccess(res);
        var body = await res.Content.ReadFromJsonAsync<UserEnvelope>();
        return body?.User;
    }

    public async Task<List<FileItem>> GetFilesAsync()
    {
        var res = await _http.GetAsync("/api/files");
        await EnsureSuccess(res);
        var body = await res.Content.ReadFromJsonAsync<FilesEnvelope>()
            ?? throw new ApiException("Empty response from server");
        return body.Files;
    }

    // The JWT cookie's raw value, so a non-HttpClient consumer (LibVLC) can
    // authenticate by query string instead - see PLAN.md's backend change.
    public string? GetAuthToken()
    {
        var cookies = _cookies.GetCookies(BaseUri);
        return cookies["token"]?.Value;
    }

    public string BuildStreamUrl(int fileId)
    {
        var token = GetAuthToken();
        var url = new Uri(BaseUri, $"/api/files/{fileId}/stream");
        return token is null ? url.ToString() : $"{url}?token={Uri.EscapeDataString(token)}";
    }

    private static async Task EnsureSuccess(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;
        string message = $"Request failed ({(int)res.StatusCode})";
        try
        {
            var err = await res.Content.ReadFromJsonAsync<ErrorEnvelope>();
            if (!string.IsNullOrWhiteSpace(err?.Error)) message = err!.Error;
        }
        catch { /* body wasn't JSON - keep the generic message */ }
        throw new ApiException(message) { StatusCode = res.StatusCode };
    }

    private class UserEnvelope
    {
        [JsonPropertyName("user")]
        public User User { get; set; } = new();
    }

    private class FilesEnvelope
    {
        [JsonPropertyName("files")]
        public List<FileItem> Files { get; set; } = [];
    }

    private class ErrorEnvelope
    {
        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}

public class ApiException(string message) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; set; }
}
