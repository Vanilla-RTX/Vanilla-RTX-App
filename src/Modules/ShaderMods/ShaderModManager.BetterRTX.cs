using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Vanilla_RTX_App.Modules.Json;
using Windows.Storage;

namespace Vanilla_RTX_App.Modules.ShaderMods;

internal class ApiPresetData
{
    public ApiPresetData() { }

    public string? Uuid { get; set; }
    public string? Slug { get; set; }
    public string? Name { get; set; }
    public string? Stub { get; set; }
    public string? Tonemapping { get; set; }
    public string? Bloom { get; set; }
}

/// <summary>
/// The BetterRTX provider: the bedrock.graphics index, its hourly staleness check, and downloading
/// one of its presets by UUID. Everything provider-neutral - the cache, reading presets off disk,
/// importing, installing - is in ShaderModManager.cs, and the Supervanilla RTX provider sits beside
/// this one in ShaderModManager.Supervanilla.cs.
///
/// <para><b>What this provider owns in the cache</b> is every folder that is neither a Default
/// backup nor Supervanilla RTX's (<see cref="IsSupervanillaFolderName"/>): its downloads, named by
/// UUID, and any import not recognised as Supervanilla RTX. A wipe triggered from here - the API
/// changing, the refresh button - touches exactly that set and nothing of Supervanilla RTX's.</para>
/// </summary>
internal sealed partial class ShaderModManager
{
    private const string ApiCacheFileName = "betterrtx_api_cache.json";
    private const string API_LAST_FETCH_KEY = "ShaderModManager_ApiLastFetchTimestamp";
    private const int API_REFETCH_INTERVAL_HOURS = 1;
    private static readonly TimeSpan ApiRequestTimeout = TimeSpan.FromSeconds(15);

    public string ApiCachePath { get; private set; } = string.Empty;

    public List<ApiPresetData>? ApiPresets { get; private set; }

    private string? _cachedApiHash = null;

    /// <summary>
    /// What the refresh button asks for: a cold start. Fetches the index, and <b>only if that
    /// worked</b> clears the downloaded and imported presets and stores the new index, so the
    /// next read comes off disk rather than making a second identical request.
    ///
    /// <para><b>The order is the whole point.</b> Wiping first and fetching afterwards is how a
    /// user with no connection loses every preset they had and cannot download any of them back.
    /// Fetching first means a failed refresh costs nothing: false comes back, nothing was
    /// touched, and the caller leaves the button live to try again.</para>
    ///
    /// <para>This deliberately wipes whatever the index says, where the hourly check only wipes
    /// when the index's hash moved - the API can ship new files without its payload changing,
    /// and forcing that case is the only reason this button exists.</para>
    /// </summary>
    public async Task<bool> RebuildFromApiAsync()
    {
        var fresh = await FetchApiDataAsync();
        if (string.IsNullOrWhiteSpace(fresh))
        {
            Trace.WriteLine("[ShaderMods] [Refresh] API unreachable - nothing cleared");
            return false;
        }

        await WipeBetterRTXPresetsAsync();
        await SaveApiCacheAsync(fresh, ComputeApiHash(fresh));

        // It is a successful check as much as a rebuild, so the hourly window starts again.
        try { ApplicationData.Current.LocalSettings.Values[API_LAST_FETCH_KEY] = DateTime.UtcNow.Ticks; }
        catch { }

        return true;
    }

    private static string ComputeApiHash(string json)
    {
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    #region BetterRTX API

    /// <summary>
    /// Called once per window open. If an hour has elapsed since the last API fetch,
    /// fetches fresh JSON and compares its hash to what we last cached.
    ///  Same hash? nothing, LoadApiDataAsync will load the cache normally
    ///  Different hash? soft wipe so stale downloaded presets are cleared
    ///  Fetch failed? defer silently to next launch (do not wipe)
    ///  Undetermined? soft wipe (safe default)
    ///
    /// <para><b>The JSON it fetched is what gets cached</b>, written back after the wipe that
    /// deleted the previous copy. The wipe is what makes this necessary - it clears the API
    /// cache along with the presets - and without the write-back <see cref="LoadApiDataAsync"/>
    /// finds no cache a moment later and fetches the identical file a second time, on every
    /// change and on every first run.</para>
    /// </summary>
    private async Task CheckApiStalenessOnStartupAsync()
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;

            // Check if an hour has elapsed since last fetch
            if (settings.Values.TryGetValue(API_LAST_FETCH_KEY, out var raw) && raw is long ticks)
            {
                var lastFetch = new DateTime(ticks, DateTimeKind.Utc);
                if ((DateTime.UtcNow - lastFetch).TotalHours < API_REFETCH_INTERVAL_HOURS)
                {
                    Trace.WriteLine("[ShaderMods] [StalenessCheck] Within hour window — skipping API check");
                    return;
                }
            }

            Trace.WriteLine("[ShaderMods] [StalenessCheck] Hour elapsed — fetching latest API JSON for comparison...");

            var freshJson = await FetchApiDataAsync();

            // Fetch failed entirely — defer, do NOT wipe
            if (string.IsNullOrWhiteSpace(freshJson))
            {
                Trace.WriteLine("[ShaderMods] [StalenessCheck] API unreachable — deferring to next launch");
                return;
            }

            // Stamp the successful fetch time now
            settings.Values[API_LAST_FETCH_KEY] = DateTime.UtcNow.Ticks;

            var freshHash = ComputeApiHash(freshJson);

            Trace.WriteLine($"[ShaderMods] [StalenessCheck] Fresh hash : {freshHash[..16]}...");
            Trace.WriteLine($"[ShaderMods] [StalenessCheck] Cached hash: {(_cachedApiHash != null ? _cachedApiHash[..16] + "..." : "none yet")}");

            // Load existing cache hash if we don't have it in memory yet
            // (e.g. first run of this method before LoadApiDataAsync has set _cachedApiHash)
            if (_cachedApiHash == null && File.Exists(ApiCachePath))
            {
                try
                {
                    var existingJson = await File.ReadAllTextAsync(ApiCachePath);
                    _cachedApiHash = ComputeApiHash(existingJson);
                    Trace.WriteLine($"[ShaderMods] [StalenessCheck] Loaded cache hash from disk: {_cachedApiHash[..16]}...");
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[ShaderMods] [StalenessCheck] Couldn't read existing cache for comparison: {ex.Message}");
                    // _cachedApiHash stays null — falls through to undetermined → wipe
                }
            }

            if (_cachedApiHash == null)
            {
                // No prior cache to compare against — undetermined, wipe to be safe
                Trace.WriteLine("[ShaderMods] [StalenessCheck] No prior cache hash — undetermined, soft wipe (safe default)");
                await WipeBetterRTXPresetsAsync();
                await SaveApiCacheAsync(freshJson, freshHash);
                return;
            }

            if (freshHash == _cachedApiHash)
            {
                Trace.WriteLine("[ShaderMods] [StalenessCheck] ✓ API unchanged — no wipe needed");
                return;
            }

            // Content changed — wipe downloaded presets so stale files don't linger
            Trace.WriteLine("[ShaderMods] [StalenessCheck] ⚠ API changed — soft wiping downloaded presets");
            await WipeBetterRTXPresetsAsync();
            await SaveApiCacheAsync(freshJson, freshHash);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [StalenessCheck] ✗ Unexpected error: {ex.Message} — soft wipe (safe default)");
            try { await WipeBetterRTXPresetsAsync(); } catch { }
        }
    }

    /// <summary>
    /// Writes API JSON already in hand to the cache and remembers its hash, so the next read
    /// comes off disk. Failing to write is not worth failing the open over - the next read
    /// fetches, which is what would have happened anyway.
    /// </summary>
    private async Task SaveApiCacheAsync(string json, string hash)
    {
        try
        {
            await File.WriteAllTextAsync(ApiCachePath, json);
            _cachedApiHash = hash;
            Trace.WriteLine("[ShaderMods] [StalenessCheck] Cached the JSON this check fetched");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] [StalenessCheck] Couldn't cache fetched JSON: {ex.Message}");
        }
    }

    public async Task LoadApiDataAsync()
    {
        try
        {
            string? jsonData = null;
            bool loadedFromCache = false;

            // Check if cache exists and is valid
            if (File.Exists(ApiCachePath))
            {
                Trace.WriteLine("[ShaderMods] ✓ Loading API data from cache...");
                try
                {
                    jsonData = await File.ReadAllTextAsync(ApiCachePath);

                    if (!string.IsNullOrWhiteSpace(jsonData))
                    {
                        // Parse ONCE and validate
                        var parsedPresets = ParseApiData(jsonData);
                        if (parsedPresets != null && parsedPresets.Count > 0)
                        {
                            ApiPresets = parsedPresets;
                            loadedFromCache = true;
                            Trace.WriteLine($"[ShaderMods] ✓ Cache is valid with {ApiPresets.Count} presets");
                        }
                        else
                        {
                            Trace.WriteLine("[ShaderMods] ⚠ Cache exists but is empty or invalid - will fetch fresh data");
                            jsonData = null;
                        }
                    }
                    else
                    {
                        Trace.WriteLine("[ShaderMods] ⚠ Cache file is empty - will fetch fresh data");
                        jsonData = null;
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[ShaderMods] ⚠ Error reading/parsing cache: {ex.Message} - will fetch fresh data");
                    jsonData = null;
                    loadedFromCache = false;
                }
            }

            // If no valid cache, fetch from API
            if (!loadedFromCache)
            {
                Trace.WriteLine("[ShaderMods] Fetching API data from server...");
                jsonData = await FetchApiDataAsync();

                if (jsonData != null && !string.IsNullOrWhiteSpace(jsonData))
                {
                    // Parse ONCE and validate
                    var parsedPresets = ParseApiData(jsonData);
                    if (parsedPresets != null && parsedPresets.Count > 0)
                    {
                        ApiPresets = parsedPresets;

                        // Save to cache
                        try
                        {
                            await File.WriteAllTextAsync(ApiCachePath, jsonData);
                            Trace.WriteLine("[ShaderMods] ✓ API data cached successfully");
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"[ShaderMods] ⚠ Failed to save cache: {ex.Message}");
                        }
                    }
                    else
                    {
                        Trace.WriteLine("[ShaderMods] ⚠ Fetched data is empty or invalid - not caching");
                        ApiPresets = new List<ApiPresetData>();
                    }
                }
                else
                {
                    Trace.WriteLine("[ShaderMods] ⚠ Failed to fetch API data and no valid cache available");
                    ApiPresets = new List<ApiPresetData>();
                }
            }

            Trace.WriteLine($"[ShaderMods] ✓ Loaded {ApiPresets?.Count ?? 0} presets total");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] Error in LoadApiDataAsync: {ex.Message}");
            ApiPresets = new List<ApiPresetData>();
        }
    }

    private async Task<string?> FetchApiDataAsync()
    {
        try
        {
            var client = Helpers.SharedHttpClient;

            // Both shared clients have their own timeout disabled, so a deadline has to come from
            // the caller - without one a server that accepts the connection and then says nothing
            // leaves the manager on its loading screen for as long as the window is open.
            using var cts = new CancellationTokenSource(ApiRequestTimeout);
            var response = await client.GetAsync("https://bedrock.graphics/api", cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                Trace.WriteLine($"[ShaderMods] ⚠ API returned status code: {response.StatusCode}");
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrWhiteSpace(content))
            {
                Trace.WriteLine("[ShaderMods] ⚠ API returned empty response");
                return null;
            }

            return content;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            Trace.WriteLine("[ShaderMods] ⚠ API request timed out");
            return null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] ⚠ Error fetching API data: {ex.Message}");
            return null;
        }
    }

    private List<ApiPresetData> ParseApiData(string jsonData)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jsonData))
            {
                Trace.WriteLine("[ShaderMods] ⚠ Cannot parse null or empty JSON data");
                return new List<ApiPresetData>();
            }

            var presets = new List<ApiPresetData>();

            // Walked as nodes rather than deserialized into ApiPresetData: the DTO's setters are
            // only ever called reflectively, which a trimmed Release build strips, and the API is
            // a third party's - an entry of an unexpected shape should cost that entry, not the
            // whole list.
            var jsonArray = MinecraftJson.ParseArray(jsonData);
            if (jsonArray == null)
            {
                Trace.WriteLine("[ShaderMods] ⚠ API response was not a JSON array");
                return presets;
            }

            foreach (var item in jsonArray)
            {
                if (item is not System.Text.Json.Nodes.JsonObject entry) continue;

                presets.Add(new ApiPresetData
                {
                    Uuid = MinecraftJson.GetString(entry["uuid"]),
                    Slug = MinecraftJson.GetString(entry["slug"]),
                    Name = MinecraftJson.GetString(entry["name"]),
                    Stub = MinecraftJson.GetString(entry["stub"]),
                    Tonemapping = MinecraftJson.GetString(entry["tonemapping"]),
                    Bloom = MinecraftJson.GetString(entry["bloom"])
                });
            }

            return presets;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] Error parsing API data: {ex.Message}");
            return new List<ApiPresetData>();
        }
    }

    #endregion

    #region BetterRTX downloads

    /// <summary>
    /// Fetches one preset from the API and unpacks it into its own folder in the cache,
    /// named after its UUID so re-downloading replaces it in place.
    /// </summary>
    /// <param name="cancellationToken">
    /// The caller's lifetime, not ours - a three-minute download has to stop when the
    /// window it was started from closes.
    /// </param>
    public async Task<bool> DownloadPresetAsync(string uuid, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://bedrock.graphics/pack/{uuid}/release";
            Trace.WriteLine($"[ShaderMods] Downloading from: {url}");

            var (success, downloadedPath) = await Helpers.Download(url, cancellationToken: cancellationToken, timeout: TimeSpan.FromMinutes(3));

            if (!success || string.IsNullOrEmpty(downloadedPath))
            {
                Trace.WriteLine($"[ShaderMods] ✗ Download failed");
                return false;
            }

            Trace.WriteLine($"[ShaderMods] ✓ Downloaded to: {downloadedPath}");

            // Extract to RTX_Cache
            var sanitizedName = SanitizePresetName(uuid);
            var destinationFolder = Path.Combine(CacheFolder, sanitizedName);

            // Delete existing folder if present
            if (Directory.Exists(destinationFolder))
            {
                Directory.Delete(destinationFolder, true);
            }

            Directory.CreateDirectory(destinationFolder);

            // Extract the archive
            await Task.Run(() =>
            {
                using (var archive = ZipFile.OpenRead(downloadedPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue;

                            var destPath = Path.Combine(destinationFolder, entry.FullName);
                            var destDir = Path.GetDirectoryName(destPath);

                            if (!string.IsNullOrEmpty(destDir))
                            {
                                Directory.CreateDirectory(destDir);
                            }

                            entry.ExtractToFile(destPath, true);
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"[ShaderMods] Error extracting {entry.FullName}: {ex.Message}");
                        }
                    }
                }
            });

            Trace.WriteLine($"[ShaderMods] ✓ Extracted to: {destinationFolder}");

            // Clean up downloaded file
            try
            {
                File.Delete(downloadedPath);
            }
            catch { }

            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ShaderMods] ✗ Error downloading {uuid}: {ex.Message}");
            return false;
        }
    }

    #endregion
}
