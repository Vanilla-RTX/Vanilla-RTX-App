using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vanilla_RTX_App.Core;
using Windows.Storage;
using static Vanilla_RTX_App.MainWindow;

namespace Vanilla_RTX_App.Modules;

/// <summary>What a conditional fetch came back with - see <see cref="Helpers.GetStringConditionalAsync"/>.</summary>
public enum FetchStatus
{
    /// <summary>The server sent a body. <c>Body</c> is it, and <c>ETag</c> identifies it.</summary>
    Modified,

    /// <summary>304: what the caller already has is current. Nothing was transferred.</summary>
    NotModified,

    /// <summary>Nothing usable came back. <c>Retryable</c> says whether asking again could help.</summary>
    Failed
}

/// <param name="ETag">
/// The identity of the bytes now in hand, to be stored with them through
/// <see cref="Helpers.SetValidator"/> - <b>only once they are actually committed</b>, or the
/// next conditional request claims to hold content that was never written.
/// </param>
/// <param name="Retryable">
/// False for a 4xx, where asking again immediately gets the same answer and, on a 429, makes it
/// worse.
/// </param>
public readonly record struct FetchResult(FetchStatus Status, string? Body, string? ETag, bool Retryable);

/// <param name="NotModified">
/// True when the server answered 304, which is only possible when the caller passed
/// <c>conditional: true</c> and a validator was stored. <c>Success</c> is false and
/// <c>Path</c> null in that case: nothing was downloaded because nothing needed to be.
/// </param>
public readonly record struct DownloadResult(bool Success, string? Path, bool NotModified, string? ETag)
{
    /// <summary>Lets the existing <c>var (success, path) = await Download(...)</c> call sites stand.</summary>
    public void Deconstruct(out bool success, out string? path)
    {
        success = Success;
        path = Path;
    }

    public static implicit operator (bool, string?)(DownloadResult result) => (result.Success, result.Path);
}

public static partial class Helpers
{
    public static readonly HttpClient SharedHttpClient = CreateClient();
    public static readonly HttpClient UpdaterHttpClient = CreateClient("updater");

    static Helpers()
    {
        Trace.WriteLine("[HttpsHelper] SharedHttpClient and UpdaterHttpClient configured");
    }

    /// <summary>
    /// Builds one of the app's two long-lived clients.
    ///
    /// <para><b>Timeout is infinite on purpose.</b> HttpClient.Timeout covers the whole
    /// request including the response body, so any finite value is a cap on download <i>size
    /// over speed</i> rather than on hanging - an 11MB pack on a slow line would be cancelled
    /// mid-transfer. Per-request deadlines belong to the caller instead, via a
    /// CancellationToken (see <see cref="Download"/>, which takes its own timeout and turns it
    /// into one).</para>
    /// </summary>
    private static HttpClient CreateClient(string? component = null)
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Add("User-Agent", BuildUserAgent(component));
        return client;
    }

    /// <summary>
    /// Builds the app's User-Agent string. Pass a component name (e.g. "updater")
    /// to tag requests from a specific feature; omit it for the default app-wide UA.
    /// </summary>
    public static string BuildUserAgent(string? component = null) =>
        component is null
            ? $"vanilla_rtx_app/{EnvironmentVariables.appVersion}"
            : $"vanilla_rtx_app_{component}/{EnvironmentVariables.appVersion} (https://github.com/vanilla-rtx/Vanilla-RTX-App)";
    /// <summary>
    /// Where vanillartx.net lives, in order of preference. Both are the same Cloudflare
    /// deployment, both edge-cached (<c>Cache-Control: max-age=300</c>, identical ETags -
    /// measured), so the second is purely the way through when the domain itself stops resolving.
    /// </summary>
    public static readonly string[] VanillaRtxSiteOrigins =
    [
        "https://vanillartx.net",
        "https://vanilla-rtx-website.cubeir.workers.dev"
    ];

    /// <summary>
    /// <paramref name="pathAndQuery"/> on the first <see cref="VanillaRtxSiteOrigins"/> entry that
    /// answers at all, for opening a page of the site in a browser. Any HTTP status counts as an
    /// answer - the question is only whether the host is up, and a page that 404s on the domain
    /// would 404 on the fallback too. When nothing answers, the first origin is returned anyway,
    /// so the browser shows its own error for the address the user would recognise.
    ///
    /// <para>A short deadline per origin because a person is waiting on a click: the domain
    /// normally answers in well under a second, and one that hasn't in 3 is not about to.</para>
    /// </summary>
    public static async Task<string> ResolveVanillaRtxSiteUrlAsync(string pathAndQuery = "")
    {
        foreach (var origin in VanillaRtxSiteOrigins)
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var request = new HttpRequestMessage(HttpMethod.Head, origin + "/");
                using var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                return origin + pathAndQuery;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[HttpsHelper] {origin} didn't answer ({ex.GetType().Name}) - trying the next origin");
            }
        }
        return VanillaRtxSiteOrigins[0] + pathAndQuery;
    }

    /// <summary>
    /// How long a transfer may make no progress at all before it is abandoned and retried.
    /// Separate from <c>timeout</c>, which bounds the whole attempt: a total deadline large
    /// enough for an 11MB pack on a slow line is far too large to notice a connection that was
    /// accepted and then went silent, and one small enough to notice that would cancel the pack.
    /// </summary>
    private static readonly TimeSpan NoProgressTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether a failed request is one the server answered deliberately (4xx), as opposed to a
    /// transport failure or a server-side fault. <c>EnsureSuccessStatusCode</c> is what puts the
    /// code on the exception; a request that never reached a server carries none, and counts as
    /// transient.
    /// </summary>
    public static bool IsClientError(HttpRequestException ex) =>
        ex.StatusCode is { } code && (int)code >= 400 && (int)code < 500;

    // ── Conditional requests ──────────────────────────────────────────────────
    //
    // Every address the app fetches from GitHub carries an ETag, and answers a request that
    // quotes it back with an empty 304. Measured against raw.githubusercontent.com: markdown,
    // JSON and a zip all return 304 for a matching validator and a full 200 for a stale one.
    // bedrock.graphics sends no validator at all, so the BetterRTX index cannot use this.
    //
    // It does not reduce the request count - it removes the body, which is what makes a short
    // cooldown affordable rather than making it unnecessary.

    private const string ValidatorKeyPrefix = "HttpValidator_";

    /// <summary>
    /// Used when there is no packaged app data to store validators in, which is every
    /// non-packaged host (a test harness). Keeping the feature alive there rather than
    /// silently disabling it is what makes it testable outside the app.
    /// </summary>
    private static readonly Dictionary<string, string> _validatorFallback = new(StringComparer.Ordinal);

    private static string ValidatorKey(string url) =>
        ValidatorKeyPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)), 0, 8);

    /// <summary>The stored validator for an address, or null when nothing is held for it.</summary>
    public static string? GetValidator(string url)
    {
        var key = ValidatorKey(url);
        try { return ApplicationData.Current.LocalSettings.Values[key] as string; }
        catch { lock (_validatorFallback) return _validatorFallback.TryGetValue(key, out var v) ? v : null; }
    }

    /// <summary>
    /// Remembers what the bytes a caller just committed are identified as. Null clears it.
    ///
    /// <para><b>Store it only after the content it describes is safely in place.</b> A validator
    /// that outlives its content makes the next request answer 304 for a copy nobody has, and
    /// the cache stays one version behind until the file changes again.</para>
    /// </summary>
    public static void SetValidator(string url, string? etag)
    {
        var key = ValidatorKey(url);
        try
        {
            if (etag is null) ApplicationData.Current.LocalSettings.Values.Remove(key);
            else ApplicationData.Current.LocalSettings.Values[key] = etag;
        }
        catch
        {
            lock (_validatorFallback)
            {
                if (etag is null) _validatorFallback.Remove(key);
                else _validatorFallback[key] = etag;
            }
        }
    }

    /// <summary>
    /// Fetches text, quoting the stored validator for the address when there is one, so an
    /// unchanged document comes back as an empty 304. Never throws; a failure is a status rather
    /// than an exception, and the caller keeps whatever it already had.
    ///
    /// <para>The caller owns the cache, and therefore owns the validator: store
    /// <see cref="FetchResult.ETag"/> through <see cref="SetValidator"/> once the body has been
    /// written, and leave it alone on a 304 - it still describes what is on disk.</para>
    /// </summary>
    public static async Task<FetchResult> GetStringConditionalAsync(
        string url,
        TimeSpan timeout,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var client = httpClient ?? SharedHttpClient;
        var validator = GetValidator(url);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // TryAddWithoutValidation: a stored validator is echoed back exactly as the server
            // wrote it, weak prefix and all, rather than being re-parsed into a shape it might
            // not round-trip through.
            if (validator is not null)
                request.Headers.TryAddWithoutValidation("If-None-Match", validator);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            if (response.StatusCode == HttpStatusCode.NotModified)
                return new FetchResult(FetchStatus.NotModified, null, validator, true);

            if (!response.IsSuccessStatusCode)
            {
                Trace.WriteLine($"[Fetch] HTTP {(int)response.StatusCode} {response.StatusCode} from {url}");
                var status = (int)response.StatusCode;
                return new FetchResult(FetchStatus.Failed, null, null, status < 400 || status >= 500);
            }

            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            return new FetchResult(FetchStatus.Modified, body, response.Headers.ETag?.Tag, true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Trace.WriteLine($"[Fetch] Timed out fetching {url}");
            return new FetchResult(FetchStatus.Failed, null, null, true);
        }
        catch (HttpRequestException ex)
        {
            Trace.WriteLine($"[Fetch] {ex.GetType().Name} fetching {url}: {ex.Message}");
            return new FetchResult(FetchStatus.Failed, null, null, !IsClientError(ex));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Fetch] {ex.GetType().Name} fetching {url}: {ex.Message}");
            return new FetchResult(FetchStatus.Failed, null, null, true);
        }
    }

    /// <summary>
    /// Downloads a file with progress tracking and retry logic.
    /// Uses the shared HttpClient which is pre-configured.
    /// For custom timeout/headers, pass a custom HttpClient.
    /// Pass quiet: true to keep progress out of the UI log and send it to Trace instead.
    ///
    /// <para>Retries cover transient failures only. A 4xx is the server's considered answer -
    /// the file is gone, or we are being rate limited - and asking again immediately makes the
    /// second case worse, so those fail on the spot.</para>
    ///
    /// <para><paramref name="conditional"/> quotes the stored validator for the address, so a
    /// file that has not changed answers 304 and nothing is transferred - the result then
    /// carries <c>NotModified</c> and no path. The ETag of a file that <i>was</i> transferred
    /// comes back on the result for the caller to store once it has committed the file
    /// (<see cref="SetValidator"/>); this method never stores it itself, because the download
    /// landing in the Downloads folder is not the same event as the caller adopting it.</para>
    /// </summary>
    public static async Task<DownloadResult> Download(
            string url,
            CancellationToken cancellationToken = default,
            HttpClient? httpClient = null,
            TimeSpan? timeout = null,
            bool quiet = false,
            bool conditional = false)
    {
        // Background callers (AssetUpdater) download things the user never asked for and
        // shouldn't have to read about; everything else keeps reporting to the UI log.
        void Report(string message, LogLevel level)
        {
            if (quiet) Trace.WriteLine($"[Download] {message}");
            else Log(message, level);
        }

        var client = httpClient ?? SharedHttpClient;
        var retries = 3;

        while (retries-- > 0)
        {
            // Set once the destination is known and cleared once the file is complete, so
            // the finally below can tell a finished download from an abandoned one.
            string? partialPath = null;

            using var timeoutCts = timeout.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            timeoutCts?.CancelAfter(timeout!.Value);

            // Layered on top of the total deadline rather than replacing it: this one is pushed
            // back by every chunk that arrives, so it only fires when nothing is coming through.
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts?.Token ?? cancellationToken);
            stallCts.CancelAfter(NoProgressTimeout);
            var token = stallCts.Token;

            try
            {
                // === DOWNLOAD ===
                using var request = new HttpRequestMessage(HttpMethod.Get, url);

                var validator = conditional ? GetValidator(url) : null;
                if (validator is not null)
                    request.Headers.TryAddWithoutValidation("If-None-Match", validator);

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    Report("Already up to date, nothing to download.", LogLevel.Cache);
                    return new DownloadResult(false, null, NotModified: true, validator);
                }

                response.EnsureSuccessStatusCode();
                Report("Starting Download.", LogLevel.Lengthy);

                var totalBytes = response.Content.Headers.ContentLength;
                if (!totalBytes.HasValue)
                    Report("Total file size unknown. Progress will be logged as total downloaded (in MegaBytes).", LogLevel.Informational);

                // === FILENAME EXTRACTION AND SANITIZATION ===
                string fileName;
                if (response.Content.Headers.ContentDisposition?.FileName != null)
                {
                    fileName = response.Content.Headers.ContentDisposition.FileName.Trim('"');
                }
                else
                {
                    fileName = Path.GetFileName(new Uri(url).AbsolutePath);
                    if (string.IsNullOrEmpty(fileName))
                    {
                        fileName = $"download_{Guid.NewGuid():N}";
                        Report($"No valid filename found, using random name: {fileName}", LogLevel.Informational);
                    }
                    else
                    {
                        Report("File name: " + fileName, LogLevel.Informational);
                    }
                }

                // sanitize filename
                fileName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
                if (fileName.Length > 128) fileName = fileName.Substring(0, 128);

                // === LOCATION RESOLUTION ===
                string? savingLocation = null;

                try
                {
                    var localFolder = LocalStateFolder ?? throw new IOException("No writable local state folder.");
                    var downloadDir = Path.Combine(localFolder, "Downloads");
                    Directory.CreateDirectory(downloadDir);

                    var finalPath = Path.Combine(downloadDir, fileName);
                    var counter = 1;
                    var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                    var extension = Path.GetExtension(fileName);

                    while (File.Exists(finalPath))
                    {
                        var newFileName = $"{fileNameWithoutExt}-{counter}{extension}";
                        finalPath = Path.Combine(downloadDir, newFileName);
                        counter++;
                    }

                    savingLocation = finalPath;
                    Report($"Save location: {savingLocation}", LogLevel.Cache);
                }
                catch (Exception ex)
                {
                    Report($"Failed to establish save location: {ex.Message}", LogLevel.Error);
                    savingLocation = null;
                }

                if (savingLocation == null)
                {
                    Report("No writable location found for download.", LogLevel.Error);
                    return new DownloadResult(false, null, NotModified: false, null);
                }

                // === DOWNLOAD WITH PROGRESS TRACKING ===
                partialPath = savingLocation;

                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(savingLocation, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);

                var buffer = new byte[8192];
                long totalRead = 0;
                int read;
                double lastLoggedProgress = 0;
                var lastLoggedMB = 0;

                while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), token);
                    totalRead += read;
                    stallCts.CancelAfter(NoProgressTimeout);

                    if (totalBytes.HasValue)
                    {
                        var progress = (double)totalRead / totalBytes.Value * 100;
                        if (progress - lastLoggedProgress >= 10 || progress >= 100)
                        {
                            lastLoggedProgress = progress;
                            Report($"Download Progress: {progress:0}%", LogLevel.Informational);
                        }
                    }
                    else
                    {
                        var currentMB = (int)(totalRead / (1024 * 1024));
                        if (currentMB > lastLoggedMB)
                        {
                            lastLoggedMB = currentMB;
                            Report($"Download Progress: {currentMB} MB", LogLevel.Informational);
                        }
                    }
                }

                partialPath = null;

                Report("Download finished successfully.", LogLevel.Success);
                return new DownloadResult(true, savingLocation, NotModified: false, response.Headers.ETag?.Tag);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Report("Download cancelled by the caller.", LogLevel.Informational);
                return new DownloadResult(false, null, NotModified: false, null);
            }
            catch (HttpRequestException ex) when (IsClientError(ex))
            {
                Report($"Download refused by the server ({(int)ex.StatusCode!.Value} {ex.StatusCode}).", LogLevel.Error);
                return new DownloadResult(false, null, NotModified: false, null);
            }
            catch (HttpRequestException ex) when (retries > 0)
            {
                Report($"Transient error: {ex.Message}. Retrying...", LogLevel.Warning);
                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException) when (retries > 0)
            {
                Report("Request timed out. Retrying...", LogLevel.Warning);
                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Report("Request timed out after all retries.", LogLevel.Error);
                return new DownloadResult(false, null, NotModified: false, null);
            }
            catch (Exception ex)
            {
                Report($"Error during download: {ex.Message}", LogLevel.Error);
                return new DownloadResult(false, null, NotModified: false, null);
            }
            finally
            {
                // A dropped connection leaves a half-written file behind, and the naming pass
                // above uniquifies around whatever already exists - so without this, retrying
                // a download that keeps failing leaves foo.json, foo-1.json, foo-2.json... in
                // the Downloads folder forever.
                if (partialPath is not null)
                {
                    try { File.Delete(partialPath); } catch { }
                }
            }
        }

        Report("Download failed after multiple attempts.", LogLevel.Error);
        return new DownloadResult(false, null, NotModified: false, null);
    }
}
