using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanDisk.Core.Model;

namespace LanDisk.Tests.Infrastructure;

public sealed record ApiResult<T>(HttpStatusCode Status, T Body, string Raw)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;
    public string ErrorCode => TryError(Raw);
    public string ErrorMessage => TryErrorMessage(Raw);

    private static string TryError(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.GetProperty("error").GetProperty("code").GetString() ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private static string TryErrorMessage(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? string.Empty;
        }
        catch { return string.Empty; }
    }
}

/// <summary>A thin, explicit HTTP client for one browser/device identity.</summary>
public sealed class LanDiskClient
{
    public HttpClient Http { get; }
    public string CsrfToken { get; private set; } = string.Empty;

    public LanDiskClient(HttpClient http) => Http = http;

    public void SetCsrf(string token) => CsrfToken = token;

    private HttpRequestMessage Build(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-LanDisk-Client", "test");
        if (CsrfToken.Length > 0) request.Headers.Add("X-LanDisk-CSRF", CsrfToken);
        return request;
    }

    public async Task<ApiResult<T>> SendJsonAsync<T>(HttpMethod method, string url, object? requestBody = null)
    {
        using HttpRequestMessage request = Build(method, url);
        if (requestBody is not null)
        {
            string json = JsonSerializer.Serialize(requestBody, TestHost.Json);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        string raw = await response.Content.ReadAsStringAsync();
        T body = default!;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try { body = JsonSerializer.Deserialize<T>(raw, TestHost.Json)!; }
            catch { body = default!; }
        }
        return new ApiResult<T>(response.StatusCode, body, raw);
    }

    public Task<ApiResult<T>> GetAsync<T>(string url) => SendJsonAsync<T>(HttpMethod.Get, url);
    public Task<ApiResult<T>> PostAsync<T>(string url, object? body = null) => SendJsonAsync<T>(HttpMethod.Post, url, body);
    public Task<ApiResult<T>> PatchAsync<T>(string url, object? body = null) => SendJsonAsync<T>(HttpMethod.Patch, url, body);
    public Task<ApiResult<T>> PutAsync<T>(string url, object? body = null) => SendJsonAsync<T>(HttpMethod.Put, url, body);
    public Task<ApiResult<T>> DeleteAsync<T>(string url) => SendJsonAsync<T>(HttpMethod.Delete, url);

    public async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request)
    {
        request.Headers.Add("X-LanDisk-Client", "test");
        if (CsrfToken.Length > 0 && !request.Headers.Contains("X-LanDisk-CSRF"))
            request.Headers.Add("X-LanDisk-CSRF", CsrfToken);
        return await Http.SendAsync(request);
    }

    /// <summary>
    /// Sends a request without buffering the response body, so a test can measure server side
    /// memory behaviour instead of the client's own response buffering.
    /// </summary>
    public async Task<HttpResponseMessage> SendStreamingAsync(HttpRequestMessage request)
    {
        request.Headers.Add('X' + "-LanDisk-Client", "test");
        if (CsrfToken.Length > 0 && !request.Headers.Contains("X-LanDisk-CSRF"))
            request.Headers.Add("X-LanDisk-CSRF", CsrfToken);
        return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    // ---- high level flows ---------------------------------------------------

    public async Task AdminLoginAsync(string bootstrapToken)
    {
        ApiResult<JsonElement> result = await PostAsync<JsonElement>("/api/admin/session", new { token = bootstrapToken });
        if (!result.IsSuccess) throw new InvalidOperationException("管理登录失败：" + result.Raw);
        SetCsrf(result.Body.GetProperty("csrfToken").GetString() ?? string.Empty);
    }

    public async Task<JsonElement> CreateShareAsync(string rootPath, bool upload = true, bool rename = true, bool delete = true, string? name = null)
    {
        ApiResult<JsonElement> result = await PostAsync<JsonElement>("/api/admin/shares", new
        {
            rootPath,
            displayName = name,
            allowUpload = upload,
            allowRename = rename,
            allowDelete = delete
        });
        if (!result.IsSuccess) throw new InvalidOperationException("创建共享失败：" + result.Raw);
        return result.Body.GetProperty("share").Clone();
    }

    public async Task<string> CreatePairingTokenAsync()
    {
        ApiResult<JsonElement> result = await PostAsync<JsonElement>("/api/admin/pairing", new { });
        if (!result.IsSuccess) throw new InvalidOperationException("生成配对请求失败：" + result.Raw);
        return result.Body.GetProperty("token").GetString()!;
    }

    /// <summary>Redeems a pairing token and leaves this client authenticated as a device.</summary>
    public async Task<JsonElement> RedeemAsync(string token, string deviceName = "测试设备")
    {
        ApiResult<JsonElement> result = await PostAsync<JsonElement>("/api/pair/redeem", new { token, deviceName });
        if (!result.IsSuccess) throw new InvalidOperationException("配对失败：" + result.Raw);
        ApiResult<JsonElement> me = await GetAsync<JsonElement>("/api/me");
        if (me.IsSuccess) SetCsrf(me.Body.GetProperty("csrfToken").GetString() ?? string.Empty);
        return result.Body.GetProperty("device").Clone();
    }

    public async Task<JsonElement> MeAsync()
    {
        ApiResult<JsonElement> result = await GetAsync<JsonElement>("/api/me");
        if (!result.IsSuccess) throw new InvalidOperationException("获取会话信息失败：" + result.Raw);
        if (result.Body.TryGetProperty("csrfToken", out JsonElement csrf))
            SetCsrf(csrf.GetString() ?? string.Empty);
        return result.Body;
    }

    // ---- file operations ----------------------------------------------------

    public Task<ApiResult<JsonElement>> BrowseAsync(string shareId, string path = "", string? sort = null, bool? desc = null, int? offset = null, int? limit = null, string? filter = null)
    {
        var query = new List<string> { $"shareId={Uri.EscapeDataString(shareId)}", $"path={Uri.EscapeDataString(path)}" };
        if (sort is not null) query.Add("sort=" + sort);
        if (desc is not null) query.Add("desc=" + (desc.Value ? "true" : "false"));
        if (offset is not null) query.Add("offset=" + offset);
        if (limit is not null) query.Add("limit=" + limit);
        if (!string.IsNullOrEmpty(filter)) query.Add("filter=" + Uri.EscapeDataString(filter));
        return GetAsync<JsonElement>("/api/browse?" + string.Join('&', query));
    }

    /// <summary>Browse with arbitrary extra query parameters (search, scope, category, ranges…).</summary>
    public Task<ApiResult<JsonElement>> BrowseExAsync(string shareId, string? path = null, params (string Key, string? Value)[] extra)
    {
        var query = new List<string> { $"shareId={Uri.EscapeDataString(shareId)}" };
        if (path is not null) query.Add("path=" + Uri.EscapeDataString(path));
        foreach ((string key, string? value) in extra)
        {
            if (value is null) continue;
            query.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        }
        return GetAsync<JsonElement>("/api/browse?" + string.Join('&', query));
    }

    /// <summary>Names of the entries in a browse response, in the order returned.</summary>
    public static string[] Names(ApiResult<JsonElement> result)
        => result.Body.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)
            .ToArray();

    public static string[] Names(JsonElement listing)
        => listing.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)
            .ToArray();

    public static string FileQuery(string shareId, string path, string? extra = null)
    {
        string query = $"?shareId={Uri.EscapeDataString(shareId)}&path={Uri.EscapeDataString(path)}";
        return extra is null ? query : query + "&" + extra;
    }
}

public static class Sandbox
{
    public static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static byte[] Pattern(int length, int seed = 1)
    {
        var bytes = new byte[length];
        uint state = (uint)(seed * 2654435761u + 1);
        for (int i = 0; i < length; i++)
        {
            state = state * 1664525u + 1013904223u;
            bytes[i] = (byte)(state >> 24);
        }
        return bytes;
    }

    /// <summary>
    /// Client side content fingerprint, matching the definition documented for upload sessions:
    /// SHA-256 over "landisk-v1\n{size}\n{lastModifiedMs}\n{sha256(head)}\n{sha256(tail)}".
    /// </summary>
    public static string Fingerprint(long size, long lastModifiedMs, byte[] content)
    {
        const int sample = 64 * 1024;
        byte[] head = content.AsSpan(0, (int)Math.Min(sample, content.Length)).ToArray();
        byte[] tail = content.Length > sample
            ? content.AsSpan(Math.Max(sample, content.Length - sample)).ToArray()
            : [];

        string material = string.Join('\n',
            "landisk-v1",
            size.ToString(),
            lastModifiedMs.ToString(),
            Sha256Hex(head),
            Sha256Hex(tail));

        return Sha256Hex(Encoding.UTF8.GetBytes(material));
    }

    public static long ToUnixMs(DateTimeOffset value) => value.ToUnixTimeMilliseconds();
}
