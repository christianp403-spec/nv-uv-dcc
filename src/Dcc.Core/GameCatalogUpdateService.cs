using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace NvUv.Dcc.Core;

public sealed record GameCatalogUpdateResult(
    bool Success,
    bool Updated,
    string Message,
    int GameCount = 0);

public sealed class GameCatalogUpdateService : IDisposable
{
    public const string DefaultUpdateUrl =
        "https://raw.githubusercontent.com/christianp403-spec/nv-uv-docs/main/games/GameDatabase.json";

    private const int MaxPayloadBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);
    private readonly HttpClient _http;
    private readonly string _updateUrl;

    public GameCatalogUpdateService(string? updateUrl = null, HttpMessageHandler? handler = null)
    {
        _updateUrl = string.IsNullOrWhiteSpace(updateUrl) ? DefaultUpdateUrl : updateUrl;
        handler ??= new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DCC-by-NV-UV/0.1 (+https://github.com/christianp403-spec/nv-uv-docs)");
    }

    public bool IsAutomaticCheckDue()
    {
        UpdateMeta meta = LoadMeta();
        return meta.LastCheckedUtc == default || DateTimeOffset.UtcNow - meta.LastCheckedUtc >= AutomaticCheckInterval;
    }

    public async Task<GameCatalogUpdateResult> CheckAndApplyAsync(CancellationToken cancellationToken = default)
    {
        UpdateMeta meta = LoadMeta();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _updateUrl);
            if (!string.IsNullOrWhiteSpace(meta.ETag))
                request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(meta.ETag));

            using HttpResponseMessage response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                SaveMeta(meta with { LastCheckedUtc = DateTimeOffset.UtcNow });
                return new GameCatalogUpdateResult(true, false, "Spieleliste ist aktuell.");
            }

            if (!response.IsSuccessStatusCode)
                return new GameCatalogUpdateResult(false, false, $"Update-Server: {(int)response.StatusCode} {response.ReasonPhrase}");

            byte[] payload = await ReadBoundedPayloadAsync(response.Content, cancellationToken).ConfigureAwait(false);
            string sha256 = Convert.ToHexString(SHA256.HashData(payload));
            int gameCount = ValidatePayload(payload);

            if (string.Equals(meta.Sha256, sha256, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(DccPaths.SharedGameDatabaseFile))
            {
                SaveMeta(meta with
                {
                    ETag = response.Headers.ETag?.Tag,
                    LastCheckedUtc = DateTimeOffset.UtcNow
                });
                return new GameCatalogUpdateResult(true, false, "Spieleliste ist aktuell.", gameCount);
            }

            Directory.CreateDirectory(DccPaths.DataDirectory);
            string temporary = DccPaths.SharedGameDatabaseFile + ".dcc.tmp";
            File.WriteAllBytes(temporary, payload);
            File.Move(temporary, DccPaths.SharedGameDatabaseFile, overwrite: true);

            SaveMeta(new UpdateMeta(
                response.Headers.ETag?.Tag,
                sha256,
                DateTimeOffset.UtcNow));
            return new GameCatalogUpdateResult(true, true, $"Spieleliste aktualisiert: {gameCount} Spiele.", gameCount);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GameCatalogUpdateResult(false, false, "Update-Zeitlimit erreicht.");
        }
        catch (HttpRequestException)
        {
            return new GameCatalogUpdateResult(false, false, "Spielelisten-Update derzeit offline.");
        }
        catch (Exception ex)
        {
            return new GameCatalogUpdateResult(false, false, $"Spielelisten-Update fehlgeschlagen: {ex.Message}");
        }
    }

    public static int ValidatePayload(ReadOnlySpan<byte> payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload.ToArray());
        if (!document.RootElement.TryGetProperty("games", out JsonElement games) ||
            games.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Die geladene Datei enthält keine Spieleliste.");
        }

        int validEntries = 0;
        foreach (JsonElement game in games.EnumerateArray())
        {
            if (game.TryGetProperty("name", out JsonElement name) &&
                name.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(name.GetString()) &&
                game.TryGetProperty("process", out JsonElement process) &&
                process.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(process.GetString()))
            {
                validEntries++;
            }
        }

        if (validEntries < 100)
            throw new InvalidDataException("Die geladene Spieleliste ist unerwartet klein und wurde abgelehnt.");
        return validEntries;
    }

    private static async Task<byte[]> ReadBoundedPayloadAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxPayloadBytes)
            throw new InvalidDataException("Die Spieleliste überschreitet das Größenlimit.");

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (output.Length + read > MaxPayloadBytes)
                throw new InvalidDataException("Die Spieleliste überschreitet das Größenlimit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static UpdateMeta LoadMeta()
    {
        try
        {
            if (!File.Exists(DccPaths.GameUpdateMetaFile))
                return new UpdateMeta(null, null, default);
            return JsonSerializer.Deserialize<UpdateMeta>(File.ReadAllBytes(DccPaths.GameUpdateMetaFile))
                   ?? new UpdateMeta(null, null, default);
        }
        catch
        {
            return new UpdateMeta(null, null, default);
        }
    }

    private static void SaveMeta(UpdateMeta meta)
    {
        Directory.CreateDirectory(DccPaths.DataDirectory);
        string temporary = DccPaths.GameUpdateMetaFile + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(meta, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, DccPaths.GameUpdateMetaFile, overwrite: true);
    }

    public void Dispose() => _http.Dispose();

    private sealed record UpdateMeta(string? ETag, string? Sha256, DateTimeOffset LastCheckedUtc);
}
