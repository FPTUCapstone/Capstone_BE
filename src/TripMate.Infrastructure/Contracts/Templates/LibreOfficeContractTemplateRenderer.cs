using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.ContractTemplates.Common;

namespace TripMate.Infrastructure.Contracts.Templates;

/// <summary>
/// Adapter for an explicitly configured isolated LibreOffice executable. No renderer is
/// bundled and no fake PDF is returned when deployment has not provided the engine.
/// </summary>
public sealed class LibreOfficeContractTemplateRenderer(IOptions<ContractTemplateRendererOptions> options) : IContractTemplatePdfRenderer
{
    public async Task<Result<RenderedContractTemplate>> RenderPreviewAsync(byte[] docxBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = options.Value;
        if (!configuration.IsolatedExecutionVerified || !Executable(configuration.ExecutablePath) || !Executable(configuration.PdfTextExtractorPath)) return Unavailable();
        var work = Path.Combine(Path.GetTempPath(), "tripmate-contract-render", Guid.NewGuid().ToString("N"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.TimeoutSeconds, 1, 60)));
        try
        {
            Directory.CreateDirectory(work);
            var input = Path.Combine(work, "preview.docx");
            await File.WriteAllBytesAsync(input, docxBytes, timeout.Token);
            var start = new ProcessStartInfo(configuration.ExecutablePath)
            {
                WorkingDirectory = work,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "--headless", "--norestore", "--nodefault", "--nolockcheck", "-env:UserInstallation=" + new Uri(Path.Combine(work, "profile")).AbsoluteUri, "--convert-to", "pdf:writer_pdf_Export", "--outdir", work, input }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return Unavailable();
            // Drain both pipes concurrently; renderer diagnostics are not sent to users/logs.
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            await Task.WhenAll(output, errors);
            var pdfPath = Path.Combine(work, "preview.pdf");
            if (process.ExitCode != 0 || !File.Exists(pdfPath)) return Unavailable();
            if (new FileInfo(pdfPath).Length > DocxContractTemplateProcessor.MaximumExpandedBytes) return Unavailable();
            var bytes = await File.ReadAllBytesAsync(pdfPath, timeout.Token);
            if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) return Unavailable();
            var boundingPath = Path.Combine(work, "preview-bounds.html");
            var extract = new ProcessStartInfo(configuration.PdfTextExtractorPath)
            {
                WorkingDirectory = work,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-bbox", "-enc", "UTF-8", pdfPath, boundingPath }) extract.ArgumentList.Add(argument);
            using var extractor = Process.Start(extract);
            if (extractor is null) return Unavailable();
            var extractOutput = extractor.StandardOutput.ReadToEndAsync(timeout.Token);
            var extractErrors = extractor.StandardError.ReadToEndAsync(timeout.Token);
            try { await extractor.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!extractor.HasExited) extractor.Kill(entireProcessTree: true);
                throw;
            }
            await Task.WhenAll(extractOutput, extractErrors);
            if (extractor.ExitCode != 0 || !File.Exists(boundingPath)) return Unavailable();
            var bounds = await File.ReadAllTextAsync(boundingPath, timeout.Token);
            var anchors = ContractTemplatePdfAnchors.Parse(bounds);
            if (anchors.Count == 0) return Unavailable();
            return Result<RenderedContractTemplate>.Success(new(bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), true) { SignatureAnchors = anchors });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or OperationCanceledException) { return Unavailable(); }
        finally
        {
            // Only the unique work directory allocated above is removed.
            try { if (Directory.Exists(work)) Directory.Delete(work, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Result<RenderedContractTemplate> Unavailable() => Result<RenderedContractTemplate>.Failure(ContractTemplateDocumentErrors.RendererUnavailable, "Document preview is temporarily unavailable. The rendering engine must be configured and verified.");

    private static bool Executable(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(path);
}