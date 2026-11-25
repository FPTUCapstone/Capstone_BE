using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;

using Microsoft.Extensions.Options;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.Admin.ContractTemplates.Common;

namespace TripMate.Infrastructure.Contracts.Templates;

/// <summary>Runs only the document engine in a resource bounded container without network access.</summary>
public sealed class DockerContractTemplateRenderer(IOptions<ContractTemplateRendererOptions> options) : IContractTemplatePdfRenderer
{
    public static IReadOnlyList<string> Arguments(string name, string image, string input, string output) =>
    ["run", "--rm", "--name", name, "--network", "none", "--memory", "768m", "--cpus", "1",
        "--pids-limit", "128", "--read-only", "--cap-drop=ALL", "--security-opt=no-new-privileges",
        "--tmpfs", "/tmp:rw,nosuid,size=128m", "--mount", $"type=bind,source={input},target=/input,readonly",
        "--mount", $"type=bind,source={output},target=/work", "--entrypoint", "/bin/sh", image, "-c",
        "soffice --headless --norestore --nodefault --nolockcheck -env:UserInstallation=file:///work/profile --convert-to pdf:writer_pdf_Export --outdir /work /input/preview.docx && pdftotext -bbox -enc UTF-8 /work/preview.pdf /work/bounds.html"];

    public async Task<Result<RenderedContractTemplate>> RenderPreviewAsync(byte[] docxBytes, CancellationToken cancellationToken)
    {
        var configuration = options.Value;
        if (!Path.IsPathFullyQualified(configuration.DockerExecutablePath) || !File.Exists(configuration.DockerExecutablePath)
            || string.IsNullOrWhiteSpace(configuration.DockerImage) || configuration.DockerImage.StartsWith('-')) return Unavailable();
        var work = Path.Combine(Path.GetTempPath(), "tripmate-contract-docker", Guid.NewGuid().ToString("N"));
        var name = "tripmate-render-" + Guid.NewGuid().ToString("N");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.TimeoutSeconds, 1, 60)));
        try
        {
            var input = Directory.CreateDirectory(Path.Combine(work, "input")).FullName;
            var output = Directory.CreateDirectory(Path.Combine(work, "output")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(input, "preview.docx"), docxBytes, timeout.Token);
            var start = new ProcessStartInfo(configuration.DockerExecutablePath)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in Arguments(name, configuration.DockerImage, input, output)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return Unavailable();
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
            await Task.WhenAll(stdout, stderr);
            var pdf = Path.Combine(output, "preview.pdf");
            var bounds = Path.Combine(output, "bounds.html");
            if (process.ExitCode != 0 || !File.Exists(pdf) || !File.Exists(bounds)
                || new FileInfo(pdf).Length > DocxContractTemplateProcessor.MaximumExpandedBytes
                || new FileInfo(bounds).Length > DocxContractTemplateProcessor.MaximumExpandedBytes) return Unavailable();
            var bytes = await File.ReadAllBytesAsync(pdf, timeout.Token);
            if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) return Unavailable();
            var anchors = ContractTemplatePdfAnchors.Parse(await File.ReadAllTextAsync(bounds, timeout.Token));
            if (anchors.Count == 0) return Unavailable();
            return Result.Success(new RenderedContractTemplate(bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), true) { SignatureAnchors = anchors });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or OperationCanceledException) { return Unavailable(); }
        finally
        {
            // Stopping the CLI alone does not stop its container. Remove only this invocation's unique container.
            try
            {
                var stop = new ProcessStartInfo(configuration.DockerExecutablePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "rm", "-f", name }) stop.ArgumentList.Add(argument);
                using var process = Process.Start(stop);
                if (process is not null)
                {
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await process.WaitForExitAsync(cleanup.Token); await Task.WhenAll(stdout, stderr); }
                    catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); }
                }
            }
            catch (Exception exception) when (exception is IOException or Win32Exception or UnauthorizedAccessException) { }
            try { if (Directory.Exists(work)) Directory.Delete(work, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Result<RenderedContractTemplate> Unavailable() => Result.Failure<RenderedContractTemplate>(
        ContractTemplateDocumentErrors.RendererUnavailable, "Document preview is temporarily unavailable.");
}