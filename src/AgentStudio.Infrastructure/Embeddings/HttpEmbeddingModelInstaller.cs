using System.Globalization;
using System.Security.Cryptography;
using AgentStudio.Application.Semantic;

namespace AgentStudio.Infrastructure.Embeddings;

/// <summary>
/// Downloads the files of a pinned model into its <see cref="EmbeddingModelLocation"/>, once and only when it is called
/// with the user's OK (docs/17 decision 9; ADR-0047 point 4). Nothing calls it on its own: the screen that asks for the OK
/// does. It is the only part of the semantic index that reaches the network, and it reads only the pinned
/// <see cref="EmbeddingModelFile.Source"/> of each file (over HTTPS; the redirects of that address are followed by the
/// handler, never to plain HTTP).
/// <list type="bullet">
/// <item>A file already in place with its pinned size and SHA-256 is kept and not downloaded again.</item>
/// <item>Any other file is downloaded to a new temporary file in the same folder, hashed as it arrives and cut as soon as
/// it is longer than its pinned size, flushed to disk and moved into place (a rename, all or nothing) only when its size
/// and SHA-256 are the pinned ones. A file that does not verify is deleted and the one in place, if any, is left as it
/// was: nothing is left half done, and a cancellation deletes the temporary file too.</item>
/// <item>The result is the state of the model after the download, with the reason in Spanish when it is not ready:
/// <see cref="EmbeddingModelState.Mismatch"/> when what arrived does not give its pinned size or hash, and
/// <see cref="EmbeddingModelState.Missing"/> when it could not be downloaded or saved (the server, the network or the
/// disk). Only a consent that is not one (<see cref="ArgumentException"/>) and a cancellation are thrown.</item>
/// <item>Two calls at once take turns; the second finds the files of the first in place.</item>
/// <item>At its end, whatever happened, the folder counts one more install (<see cref="EmbeddingModelFolderChanges"/>): what
/// a source remembered of it (its checks, a failed opening) is forgotten, so a file restored by hand with the size and
/// time of the one before is seen once the model is installed again.</item>
/// </list>
/// The <see cref="HttpClient"/> belongs to the caller, who disposes it; its timeout covers only the wait for the headers
/// of each file, and the cancellation of the call stops the download at any point.
/// </summary>
public sealed class HttpEmbeddingModelInstaller : IEmbeddingModelInstaller
{
    private const int BufferBytes = 1 << 16;

    /// <summary>How often the progress is reported while a file arrives (and once when it is complete).</summary>
    private const long ProgressStepBytes = 1 << 20;

    private const string TemporarySuffix = ".part";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <exception cref="ArgumentException">A file of the model is not downloaded over HTTPS.</exception>
    public HttpEmbeddingModelInstaller(HttpClient http, EmbeddingModelLocation location)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(location);
        foreach (var file in location.Files)
        {
            _ = location.PathOf(file);
            if (!file.Source.IsAbsoluteUri || !string.Equals(file.Source.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            {
                throw new ArgumentException($"The file '{file.FileName}' of the model is not downloaded over HTTPS: '{file.Source}'.", nameof(location));
            }
        }

        _http = http;
        Location = location;
    }

    public EmbeddingModelLocation Location { get; }

    public async Task<EmbeddingModelStatus> InstallAsync(EmbeddingModelDownloadConsent consent, IProgress<EmbeddingModelDownloadProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(consent);
        if (string.IsNullOrWhiteSpace(consent.GrantedBy) || consent.GrantedAt == default)
        {
            throw new ArgumentException("A download needs the OK of someone, with the time it was given.", nameof(consent));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                Directory.CreateDirectory(Location.ModelDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new EmbeddingModelStatus(
                    EmbeddingModelState.Missing,
                    Location.ModelDirectory,
                    $"No se ha podido crear la carpeta del modelo: {ex.Message.TrimEnd().TrimEnd('.')}.");
            }

            foreach (var file in Location.Files)
            {
                ct.ThrowIfCancellationRequested();
                DeleteLeftovers(file);
                var (state, _) = await Task.Run(() => EmbeddingModelFiles.CheckFile(Location, file, ct), ct).ConfigureAwait(false);
                if (state == EmbeddingModelState.Ready)
                {
                    progress?.Report(new EmbeddingModelDownloadProgress(file.FileName, file.Bytes, file.Bytes));
                    continue;
                }

                if (await DownloadAsync(file, progress, ct).ConfigureAwait(false) is { } failure)
                {
                    return failure;
                }
            }

            return await Task.Run(() => EmbeddingModelFiles.Check(Location, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            // Whatever it did, the checks and the failed openings remembered of this folder are forgotten.
            EmbeddingModelFolderChanges.Changed(Location.ModelDirectory);
            _gate.Release();
        }
    }

    /// <summary>Downloads <paramref name="file"/> and moves it into place when it verifies; otherwise the reason it did not, and nothing changed.</summary>
    private async Task<EmbeddingModelStatus?> DownloadAsync(EmbeddingModelFile file, IProgress<EmbeddingModelDownloadProgress>? progress, CancellationToken ct)
    {
        var target = Location.PathOf(file);
        var temporary = string.Create(CultureInfo.InvariantCulture, $"{target}.{Guid.NewGuid():N}{TemporarySuffix}");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, file.Source);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Missing(file, string.Create(CultureInfo.InvariantCulture, $"el servidor respondió {(int)response.StatusCode} ({response.ReasonPhrase})"));
            }

            if (response.Content.Headers.ContentLength is { } announced && announced != file.Bytes)
            {
                return Mismatch(EmbeddingModelFiles.SizeText(file, announced));
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.Asynchronous);
                await using (output.ConfigureAwait(false))
                {
                    var buffer = new byte[BufferBytes];
                    var reported = 0L;
                    progress?.Report(new EmbeddingModelDownloadProgress(file.FileName, 0, file.Bytes));
                    int read;
                    while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        received += read;
                        if (received > file.Bytes)
                        {
                            // Never read an answer without a bound: what is longer than the pinned file is not it.
                            return Mismatch(EmbeddingModelFiles.SizeText(file, received));
                        }

                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        if (received - reported >= ProgressStepBytes)
                        {
                            reported = received;
                            progress?.Report(new EmbeddingModelDownloadProgress(file.FileName, received, file.Bytes));
                        }
                    }

                    await output.FlushAsync(ct).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
            }

            progress?.Report(new EmbeddingModelDownloadProgress(file.FileName, received, file.Bytes));
            if (received != file.Bytes)
            {
                return Mismatch(EmbeddingModelFiles.SizeText(file, received));
            }

            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), file.Sha256, StringComparison.Ordinal))
            {
                return Mismatch(EmbeddingModelFiles.HashText(file));
            }

            // Same folder, same volume: replacing is a rename, which happens entirely or not at all.
            File.Move(temporary, target, overwrite: true);
            return null;
        }
        catch (HttpRequestException ex)
        {
            return Missing(file, ex.StatusCode is { } code ? string.Create(CultureInfo.InvariantCulture, $"{(int)code} ({ex.Message})") : ex.Message);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // The timeout of the client, not the user's cancellation.
            return Missing(file, $"la conexión tardó demasiado ({ex.Message})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Missing(file, ex.Message);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private EmbeddingModelStatus Missing(EmbeddingModelFile file, string why) =>
        new(EmbeddingModelState.Missing, Location.ModelDirectory, $"No se ha podido descargar ni guardar el fichero «{file.FileName}» del modelo: {why.TrimEnd().TrimEnd('.')}.");

    private EmbeddingModelStatus Mismatch(string problem) =>
        new(EmbeddingModelState.Mismatch, Location.ModelDirectory, problem + " La descarga se ha descartado.");

    /// <summary>The temporary files of a download that a crash or a power cut left behind: they are never read, only deleted.</summary>
    private void DeleteLeftovers(EmbeddingModelFile file)
    {
        IEnumerable<string> leftovers;
        try
        {
            leftovers = Directory.EnumerateFiles(Location.ModelDirectory, file.FileName + ".*" + TemporarySuffix, SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var leftover in leftovers)
        {
            TryDelete(leftover);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is never read, and the next install deletes it.
        }
    }
}
