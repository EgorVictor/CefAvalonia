using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

/// <summary>
/// Locates <c>libcef.so</c> on Linux.
///
/// The Linux NuGet package cannot ship <c>libcef.so</c>: it is ~1.05 GB raw and
/// still ~240 MB compressed, so any package containing it exceeds NuGet.org's
/// 250 MB hard limit. The Windows package does not have this problem because
/// <c>libcef.dll</c> is ~186 MB and the whole package stays at ~116 MB.
///
/// On Linux the runtime therefore resolves <c>libcef.so</c> in this order:
/// <list type="number">
///   <item>next to the native host executable (normal deployment),</item>
///   <item><c>CEF_ROOT</c> / <c>CEFAVALONIA_CEF_ROOT</c> override (offline provisioning),</item>
///   <item>any directory already on <c>LD_LIBRARY_PATH</c>,</item>
///   <item>the download cache under <c>~/.cache/cefavalonia/libcef/&lt;version&gt;</c>,</item>
///   <item>a one-time, checksum-verified download from a GitHub Release asset.</item>
/// </list>
///
/// The resolved directory is exposed through <see cref="ResolvedDirectory"/> and
/// added to <c>LD_LIBRARY_PATH</c> when the host process is spawned.
/// </summary>
public static class CefRuntimeResolver
{
    /// <summary>CEF version the native host was built against. Must match the downloaded binary exactly.</summary>
    public const string CefVersion = "109.1.11";

    private const string DefaultDownloadUrlFormat =
        "https://github.com/EgorVictor/CefAvalonia/releases/download/cef-runtime-{0}/libcef-{0}-linux-x64.tar.gz";

    /// <summary>
    /// SHA-256 of the release asset published at tag <c>cef-runtime-109.1.11</c>.
    /// With no checksum the download is REFUSED rather than fetching an
    /// unverified binary.
    ///
    /// This hashes the EXACT file uploaded to the release, which is immutable:
    /// re-tarring libcef.so elsewhere yields different bytes and therefore a
    /// different hash. If the asset is ever rebuilt, update this constant and
    /// the uploaded file together.
    /// </summary>
    private const string ReleaseAssetSha256 =
        "e5307c50e1f76cefef3f893ba70b1d7c86c153531d0d77987fbf7bf3878e4569";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _resolvedDirectory;

    /// <summary>Override the download URL (self-hosted mirror, intranet, ...).</summary>
    public static string DownloadUrl { get; set; } = string.Format(DefaultDownloadUrlFormat, CefVersion);

    /// <summary>
    /// Expected SHA-256 of the archive. When null or empty, downloading is refused.
    /// </summary>
    public static string? ExpectedSha256 { get; set; } = ReleaseAssetSha256;

    /// <summary>Directory containing the <c>libcef.so</c> in use, or null before resolution.</summary>
    public static string? ResolvedDirectory => _resolvedDirectory;

    /// <summary>Set to true once libcef.so had to be downloaded (first run).</summary>
    public static bool DownloadedOnThisRun { get; private set; }

    /// <summary>
    /// Ensures <c>libcef.so</c> is available and records its directory in
    /// <see cref="ResolvedDirectory"/>. Returns false when it cannot be obtained;
    /// the caller must then surface an actionable error instead of starting the
    /// host (which would otherwise die with an opaque loader error).
    /// </summary>
    public static async Task<bool> EnsureAsync(string exePath, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux()) return true;
        if (_resolvedDirectory != null) return true;

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_resolvedDirectory != null) return true;

            string? local = ProbeLocal(exePath);
            if (local != null)
            {
                _resolvedDirectory = local;
                return true;
            }

            string cacheDir = GetCacheDirectory();
            string cached = Path.Combine(cacheDir, "libcef.so");
            if (File.Exists(cached) && new FileInfo(cached).Length > 0)
            {
                CefDiagnostics.Info($"[CefRuntime] using cached libcef.so: {cached}");
                _resolvedDirectory = cacheDir;
                return true;
            }

            string? downloaded = await DownloadAsync(cacheDir, cancellationToken).ConfigureAwait(false);
            if (downloaded == null) return false;

            _resolvedDirectory = downloaded;
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Looks for a libcef.so that is already on this machine.</summary>
    private static string? ProbeLocal(string exePath)
    {
        // 1) Next to the host executable: the documented manual provisioning path.
        string? exeDir = Path.GetDirectoryName(Path.GetFullPath(exePath));
        if (exeDir != null && File.Exists(Path.Combine(exeDir, "libcef.so")))
            return exeDir;

        // 2) Explicit override pointing at a CEF distribution (root or Release/).
        foreach (string variable in new[] { "CEF_ROOT", "CEFAVALONIA_CEF_ROOT" })
        {
            string? root = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(root)) continue;

            foreach (string candidate in new[] { root, Path.Combine(root, "Release") })
            {
                if (File.Exists(Path.Combine(candidate, "libcef.so")))
                {
                    CefDiagnostics.Info($"[CefRuntime] libcef.so from {variable}: {candidate}");
                    return Path.GetFullPath(candidate);
                }
            }

            CefDiagnostics.Warn($"[CefRuntime] {variable}='{root}' does not contain libcef.so");
        }

        // 3) Already reachable through LD_LIBRARY_PATH.
        string? libraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        if (!string.IsNullOrEmpty(libraryPath))
        {
            foreach (string entry in libraryPath.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                if (File.Exists(Path.Combine(entry, "libcef.so")))
                    return Path.GetFullPath(entry);
            }
        }

        return null;
    }

    private static async Task<string?> DownloadAsync(string cacheDir, CancellationToken cancellationToken)
    {
        if (IsTruthy(Environment.GetEnvironmentVariable("CEFAVALONIA_NO_DOWNLOAD")))
        {
            CefDiagnostics.Error(
                "[CefRuntime] libcef.so not found and downloads are disabled (CEFAVALONIA_NO_DOWNLOAD). " +
                "Copy libcef.so next to CefBrowser.Native or set CEF_ROOT.");
            return null;
        }

        string url = Environment.GetEnvironmentVariable("CEFAVALONIA_LIBCEF_URL") ?? DownloadUrl;
        string? expected = Environment.GetEnvironmentVariable("CEFAVALONIA_LIBCEF_SHA256") ?? ExpectedSha256;

        if (string.IsNullOrWhiteSpace(expected))
        {
            CefDiagnostics.Error(
                "[CefRuntime] libcef.so not found and no expected SHA-256 is configured, so an unverified " +
                "binary will not be downloaded. Copy libcef.so next to CefBrowser.Native, set CEF_ROOT, or " +
                "assign CefRuntimeResolver.ExpectedSha256.");
            return null;
        }

        string archive = Path.Combine(cacheDir, "libcef.so.tar.gz.part");
        string stage = cacheDir + ".stage-" + Guid.NewGuid().ToString("N");

        try
        {
            Directory.CreateDirectory(cacheDir);

            CefDiagnostics.Info($"[CefRuntime] downloading libcef.so {CefVersion} from {url}");
            using (var http = new HttpClient { Timeout = TimeSpan.FromHours(2) })
            {
                using HttpResponseMessage response = await http.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    CefDiagnostics.Error($"[CefRuntime] download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                    return null;
                }

                using var file = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            // Hashing and extracting a ~1 GB archive is heavy: keep it off the
            // caller's thread (browser startup runs on the UI thread).
            string actual = await Task.Run(() => ComputeSha256(archive), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                CefDiagnostics.Error($"[CefRuntime] checksum mismatch, archive discarded. expected={expected} actual={actual}");
                return null;
            }

            Directory.CreateDirectory(stage);
            await Task.Run(() =>
            {
                using FileStream archiveStream = File.OpenRead(archive);
                using var gzip = new GZipStream(archiveStream, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, stage, overwriteFiles: true);
            }, cancellationToken).ConfigureAwait(false);

            string? extracted = FindLibCef(stage);
            if (extracted == null)
            {
                CefDiagnostics.Error("[CefRuntime] archive did not contain libcef.so");
                return null;
            }

            File.Move(extracted, Path.Combine(cacheDir, "libcef.so"), overwrite: true);
            DownloadedOnThisRun = true;

            long sizeMb = new FileInfo(Path.Combine(cacheDir, "libcef.so")).Length / (1024 * 1024);
            CefDiagnostics.Info($"[CefRuntime] libcef.so ready ({sizeMb} MB) at {cacheDir}");
            return cacheDir;
        }
        catch (OperationCanceledException)
        {
            CefDiagnostics.Warn("[CefRuntime] libcef.so download was cancelled");
            return null;
        }
        catch (Exception ex)
        {
            CefDiagnostics.Error("[CefRuntime] failed to obtain libcef.so", ex);
            return null;
        }
        finally
        {
            TryDeleteFile(archive);
            TryDeleteDirectory(stage);
        }
    }

    private static string? FindLibCef(string root)
    {
        string direct = Path.Combine(root, "libcef.so");
        if (File.Exists(direct)) return direct;

        foreach (string file in Directory.EnumerateFiles(root, "libcef.so", SearchOption.AllDirectories))
            return file;

        return null;
    }

    private static string GetCacheDirectory()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        string baseDir = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
            : xdg;

        return Path.Combine(baseDir, "cefavalonia", "libcef", CefVersion);
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsTruthy(string? value) =>
        value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best effort */ }
    }
}
