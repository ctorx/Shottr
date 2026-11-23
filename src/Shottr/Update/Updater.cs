using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Shottr.Update;

public enum UpdateCheckStatus
{
    UpToDate,
    /// <summary>A newer release exists. Its Asset is null when it has no build for this computer.</summary>
    Available,
    NoReleases,
    RateLimited,
    Failed,
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    ReleaseInfo? Release = null,
    ReleaseAsset? Asset = null,
    string? Reason = null);

/// <summary>The step of an update that failed, so the message can name it.</summary>
public enum UpdateStep
{
    Check,
    Download,
    Verification,
    Install,
}

public sealed class UpdateException : Exception
{
    public UpdateException(UpdateStep step, string message, Exception? inner = null) : base(message, inner) => Step = step;

    public UpdateStep Step { get; }
}

/// <summary>A downloaded and verified update, waiting in the install folder to be swapped in.</summary>
public sealed record DownloadedUpdate(string FilePath, string Version);

/// <summary>
/// The network and file side of self-update: it checks GitHub Releases, downloads and
/// verifies the new exe, swaps it in for the running one and starts it.
/// </summary>
public static class Updater
{
    public const string Repository = "ctorx/Shottr";
    private const string LatestReleaseUrl = $"https://api.github.com/repos/{Repository}/releases/latest";

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);
    // A download fails if no bytes arrive for this long. A slow but steady download is fine.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The running version, read from the Version property in Shottr.csproj.</summary>
    // Static initializers run in file order, and the client's User-Agent needs this, so it comes first.
    public static string CurrentVersion { get; } = ReadCurrentVersion();

    private static readonly HttpClient Http = CreateClient();

    private static string ReadCurrentVersion()
    {
        string? v = typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(v))
            return "0.0.0";
        int plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            // Release downloads redirect from github.com to GitHub's object storage.
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            // HttpClient.DefaultProxy follows the Windows system proxy settings.
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler)
        {
            // Each request sets its own deadline through a cancellation token instead.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        // GitHub rejects API requests that have no User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Shottr-Updater/{CurrentVersion}");
        return client;
    }

    private static string ExePath =>
        Environment.ProcessPath ?? throw new UpdateException(UpdateStep.Install, "Shottr could not find its own exe.");

    private static string OldExePath => ExePath + ".old";

    private static string DownloadPath => ExePath + ".download";

    // ---- Check ------------------------------------------------------------------------------

    /// <summary>
    /// Asks GitHub for the latest release. GitHub's "latest" endpoint skips drafts and
    /// pre-releases. Throws only OperationCanceledException, when the caller cancels.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using HttpResponseMessage response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);

            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    return new UpdateCheckResult(UpdateCheckStatus.NoReleases);
                case HttpStatusCode.Forbidden:
                case HttpStatusCode.TooManyRequests:
                    return new UpdateCheckResult(UpdateCheckStatus.RateLimited);
            }
            if (!response.IsSuccessStatusCode)
                return Failed($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");

            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!UpdateCore.TryParseLatestRelease(body, out ReleaseInfo? release, out string? error))
                return Failed(error!);

            if (UpdateCore.CompareVersions(release!.Tag, CurrentVersion) <= 0)
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, release);

            ReleaseAsset? asset = UpdateCore.SelectAsset(release, "windows", CurrentArch);
            return new UpdateCheckResult(UpdateCheckStatus.Available, release, asset);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed("GitHub did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            return Failed("Shottr could not reach GitHub. " + ex.Message);
        }
    }

    private static UpdateCheckResult Failed(string reason) => new(UpdateCheckStatus.Failed, Reason: reason);

    private static string CurrentArch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        var other => other.ToString().ToLowerInvariant(),
    };

    // ---- Download ---------------------------------------------------------------------------

    /// <summary>
    /// Downloads the asset next to the running exe and verifies its status code, size,
    /// SHA-256 and exe header. Deletes the file if anything fails. Throws UpdateException.
    /// </summary>
    public static async Task<DownloadedUpdate> DownloadUpdateAsync(
        ReleaseInfo release, ReleaseAsset asset, IProgress<double>? progress, CancellationToken ct)
    {
        string expectedHash = await FetchExpectedHashAsync(release, asset, ct).ConfigureAwait(false);
        EnsureInstallFolderWritable();

        // Downloading into the install folder keeps the final move on the same volume.
        string path = DownloadPath;
        bool ok = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
            request.Headers.Accept.ParseAdd("application/octet-stream");
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(StallTimeout);

            using HttpResponseMessage response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new UpdateException(UpdateStep.Download, $"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.");

            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength is long cl && cl != asset.Size)
                throw new UpdateException(UpdateStep.Verification,
                    $"The server reported {cl:N0} bytes, but the release lists {asset.Size:N0} bytes.");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var header = new byte[1024];
            int headerLength = 0;
            long total = 0;

            await using (Stream body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    int read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    total += read;
                    if (total > asset.Size)
                        throw new UpdateException(UpdateStep.Verification,
                            $"The download is larger than the {asset.Size:N0} bytes the release lists.");
                    if (headerLength < header.Length)
                    {
                        int n = Math.Min(read, header.Length - headerLength);
                        Buffer.BlockCopy(buffer, 0, header, headerLength, n);
                        headerLength += n;
                    }
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                    progress?.Report(asset.Size > 0 ? (double)total / asset.Size : 0);
                }
            }

            if (total != asset.Size)
                throw new UpdateException(UpdateStep.Verification,
                    $"The download stopped at {total:N0} of {asset.Size:N0} bytes.");
            if (contentLength is long length && total != length)
                throw new UpdateException(UpdateStep.Verification,
                    $"Received {total:N0} bytes, but the server said it would send {length:N0}.");

            string actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actualHash != expectedHash)
                throw new UpdateException(UpdateStep.Verification,
                    $"The downloaded file's SHA-256 does not match the release's {UpdateCore.ChecksumFileName} file. The file may be damaged or altered.");

            if (!LooksLikeWindowsExe(header.AsSpan(0, headerLength)))
                throw new UpdateException(UpdateStep.Verification, "The downloaded file is not a Windows program.");

            ok = true;
            return new DownloadedUpdate(path, release.Version);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new UpdateException(UpdateStep.Download, "The download stalled and timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateStep.Download, ex.Message, ex);
        }
        catch (IOException ex)
        {
            throw new UpdateException(UpdateStep.Download, "Shottr could not write the download. " + ex.Message, ex);
        }
        finally
        {
            if (!ok)
                TryDelete(path);
        }
    }

    /// <summary>Downloads the release's SHA256SUMS file and returns the line for this asset.</summary>
    private static async Task<string> FetchExpectedHashAsync(ReleaseInfo release, ReleaseAsset asset, CancellationToken ct)
    {
        ReleaseAsset? sums = null;
        foreach (ReleaseAsset a in release.Assets)
        {
            if (string.Equals(a.Name, UpdateCore.ChecksumFileName, StringComparison.OrdinalIgnoreCase))
                sums = a;
        }
        if (sums == null)
            throw new UpdateException(UpdateStep.Verification,
                $"The release has no {UpdateCore.ChecksumFileName} file, so Shottr cannot check the download.");
        if (sums.Size > 64 * 1024)
            throw new UpdateException(UpdateStep.Verification, $"The release's {UpdateCore.ChecksumFileName} file is too large.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        string text;
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(sums.DownloadUrl, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new UpdateException(UpdateStep.Download,
                    $"Downloading {UpdateCore.ChecksumFileName} failed: the server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            text = Encoding.UTF8.GetString(bytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new UpdateException(UpdateStep.Download, $"Downloading {UpdateCore.ChecksumFileName} timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException(UpdateStep.Download, $"Downloading {UpdateCore.ChecksumFileName} failed. {ex.Message}", ex);
        }

        return UpdateCore.ParseChecksums(text, asset.Name)
            ?? throw new UpdateException(UpdateStep.Verification,
                $"The release's {UpdateCore.ChecksumFileName} file has no checksum for {asset.Name}.");
    }

    /// <summary>Checks for the "MZ" DOS header and the "PE" signature it points to.</summary>
    private static bool LooksLikeWindowsExe(ReadOnlySpan<byte> header)
    {
        if (header.Length < 0x40 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            return false;
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..]);
        return peOffset > 0 && peOffset + 4 <= header.Length
            && header[peOffset] == (byte)'P' && header[peOffset + 1] == (byte)'E'
            && header[peOffset + 2] == 0 && header[peOffset + 3] == 0;
    }

    /// <summary>
    /// Fails early, with the reason, when Shottr cannot write to its own folder. Shottr never
    /// asks for administrator rights to update itself.
    /// </summary>
    private static void EnsureInstallFolderWritable()
    {
        string folder = Path.GetDirectoryName(ExePath)!;
        string probe = Path.Combine(folder, $".shottr-write-test-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new UpdateException(UpdateStep.Install,
                $"Shottr cannot write to its folder, {folder}. Windows only lets administrators change that folder. " +
                @"Move Shottr.exe to a folder you own, such as %LOCALAPPDATA%\Programs\Shottr, or download the update yourself from the release page.",
                ex);
        }
    }

    // ---- Install ----------------------------------------------------------------------------

    /// <summary>
    /// Swaps the downloaded exe in for the running one. Windows lets a running exe be renamed
    /// but not overwritten, so the running exe becomes Shottr.exe.old first. If the new file
    /// cannot be moved into place, the old name is restored. Deletes the download on failure.
    /// </summary>
    public static void InstallUpdate(DownloadedUpdate downloaded)
    {
        string exe = ExePath, old = OldExePath;
        try
        {
            // An earlier update may have left a .old file that its cleanup could not delete.
            if (File.Exists(old))
            {
                try
                {
                    File.Delete(old);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    throw new UpdateException(UpdateStep.Install,
                        $"Shottr could not remove the leftover file {old} from an earlier update. {ex.Message}", ex);
                }
            }

            try
            {
                File.Move(exe, old);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw new UpdateException(UpdateStep.Install, $"Shottr could not rename its running exe. {ex.Message}", ex);
            }

            try
            {
                // File.Move copies and deletes when the source is on another volume.
                File.Move(downloaded.FilePath, exe);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                try
                {
                    File.Move(old, exe);
                }
                catch (Exception rollback) when (rollback is UnauthorizedAccessException or IOException)
                {
                    throw new UpdateException(UpdateStep.Install,
                        $"Shottr could not move the new version into place ({ex.Message}), and could not restore the old one. " +
                        $"Rename {old} back to {Path.GetFileName(exe)} to repair it.", ex);
                }
                throw new UpdateException(UpdateStep.Install,
                    $"Shottr could not move the new version into place. Your current version is unchanged. {ex.Message}", ex);
            }
        }
        catch
        {
            TryDelete(downloaded.FilePath);
            throw;
        }
    }

    /// <summary>Deletes a download that will not be installed, such as when the user cancels.</summary>
    public static void Discard(DownloadedUpdate downloaded) => TryDelete(downloaded.FilePath);

    // ---- Relaunch ---------------------------------------------------------------------------

    /// <summary>
    /// Starts the newly installed exe while this process is still running. The caller exits
    /// only after this returns. Throws UpdateException if Windows cannot start the new copy.
    /// </summary>
    public static void Relaunch(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(ExePath)!,
        };
        foreach (string a in args)
            info.ArgumentList.Add(a);
        try
        {
            using Process? p = Process.Start(info);
            if (p == null)
                throw new UpdateException(UpdateStep.Install, "Windows did not start the new version.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new UpdateException(UpdateStep.Install, $"Windows could not start the new version. {ex.Message}", ex);
        }
    }

    // ---- Cleanup ----------------------------------------------------------------------------

    /// <summary>
    /// Runs at startup. Deletes Shottr.exe.old and any abandoned download on a background
    /// thread, retrying for a few seconds while the previous process finishes exiting.
    /// </summary>
    public static void CleanUpAfterUpdate()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null)
            return;
        string[] leftovers = { exe + ".old", exe + ".download" };
        Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                bool allGone = true;
                foreach (string path in leftovers)
                {
                    if (!File.Exists(path))
                        continue;
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        allGone = false;
                    }
                }
                if (allGone)
                    return;
                await Task.Delay(500).ConfigureAwait(false);
            }
        });
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Startup cleanup removes it later.
        }
    }
}
