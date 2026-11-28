using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.ContractTemplates.Common;

namespace TripMate.Infrastructure.Contracts.Templates;

/// <summary>Private authenticated transport. The API independently validates PDF bytes and signature coordinates.</summary>
public sealed class RemoteContractTemplateRenderer(HttpClient client, IOptions<ContractTemplateRendererOptions> options) : IContractTemplatePdfRenderer
{
    private const int MaximumBoundsBytes = 10 * 1024 * 1024;
    private const int MaximumResponseBytes = 61 * 1024 * 1024;

    public async Task<Result<RenderedContractTemplate>> RenderPreviewAsync(byte[] docxBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = options.Value;
        if (!ValidEndpoint(configuration.RemoteEndpoint, out var endpoint) || configuration.RemoteApiKey.Length < 32
            || docxBytes.Length is 0 or > DocxContractTemplateProcessor.MaximumCompressedBytes) return Unavailable();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.TimeoutSeconds, 1, 60)));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.RemoteApiKey);
            request.Content = new ByteArrayContent(docxBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(DocxContractTemplateProcessor.DocxContentType);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/zip"
                || response.Content.Headers.ContentLength > MaximumResponseBytes) return Unavailable();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = await ReadBoundedAsync(stream, MaximumResponseBytes, timeout.Token);
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (archive.Entries.Count != 2 || archive.Entries.Count(entry => entry.FullName == "preview.pdf") != 1
                || archive.Entries.Count(entry => entry.FullName == "bounds.html") != 1) return Unavailable();
            var pdf = await ReadEntryAsync(archive.GetEntry("preview.pdf")!, (int)DocxContractTemplateProcessor.MaximumExpandedBytes, timeout.Token);
            if (pdf.Length < 5 || !pdf.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) return Unavailable();
            var bounds = await ReadEntryAsync(archive.GetEntry("bounds.html")!, MaximumBoundsBytes, timeout.Token);
            var anchors = ContractTemplatePdfAnchors.Parse(Encoding.UTF8.GetString(bounds));
            if (anchors.Count == 0) return Unavailable();
            return Result.Success(new RenderedContractTemplate(pdf, Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant(), true)
            { SignatureAnchors = anchors });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        { return Unavailable(); }
    }

    private static bool ValidEndpoint(string value, out Uri? endpoint) =>
        Uri.TryCreate(value, UriKind.Absolute, out endpoint) && string.IsNullOrEmpty(endpoint.UserInfo)
        && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment) && endpoint.AbsolutePath == "/render"
        && (endpoint.Scheme == Uri.UriSchemeHttps || (endpoint.Scheme == Uri.UriSchemeHttp
            && (endpoint.IsLoopback || endpoint.Host.EndsWith(".railway.internal", StringComparison.OrdinalIgnoreCase))));

    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, int maximum, CancellationToken ct)
    {
        if (entry.Length > maximum) throw new InvalidDataException("Renderer output too large.");
        await using var stream = entry.Open();
        return await ReadBoundedAsync(stream, maximum, ct);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, int maximum, CancellationToken ct)
    {
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            if (destination.Length + read > maximum) throw new InvalidDataException("Renderer output too large.");
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private static Result<RenderedContractTemplate> Unavailable() => Result.Failure<RenderedContractTemplate>(
        ContractTemplateDocumentErrors.RendererUnavailable, "Document preview is temporarily unavailable.");
}