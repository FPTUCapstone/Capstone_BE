using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Options;

using TripMate.Application.Features.Admin.ContractTemplates.Common;
using TripMate.Infrastructure.Contracts.Templates;

namespace TripMate.Infrastructure.UnitTests.Contracts.Templates;

public sealed class RemoteContractTemplateRendererTests
{
    private const string Token = "local-test-token-at-least-32-characters";
    private const string Bounds = "<html><doc><page width=\"595\" height=\"842\"><word xMin=\"50\" yMin=\"700\" xMax=\"240\" yMax=\"715\">TRIPMATE_SIGNATURE_ANCHOR</word></page></doc></html>";

    [RemoteRendererFact]
    public async Task RealWorker_RendersPreparedVietnameseDocxThroughProductionAdapter()
    {
        var upload = DocxContractTemplateProcessorTests.Upload("<w:r><w:t>{{contract_number}} {{company_name}} {{tax_code}} {{representative_name}} {{signature_block}}</w:t></w:r>");
        var prepared = await new DocxContractTemplateProcessor().PreparePreviewDocxAsync(upload, new Dictionary<string, string>
        {
            ["contract_number"] = "PREVIEW-RAILWAY",
            ["company_name"] = "Công ty Đà Nẵng",
            ["tax_code"] = "0000000000",
            ["representative_name"] = "Nguyễn Văn Mẫu",
            ["signature_block"] = "unused"
        }, default);
        prepared.IsSuccess.Should().BeTrue();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        var renderer = new RemoteContractTemplateRenderer(client, Options.Create(new ContractTemplateRendererOptions
        {
            RemoteEndpoint = Environment.GetEnvironmentVariable("TRIPMATE_RENDERER_REMOTE_ENDPOINT")!,
            RemoteApiKey = Environment.GetEnvironmentVariable("TRIPMATE_RENDERER_REMOTE_KEY")!
        }));
        var result = await renderer.RenderPreviewAsync(prepared.Value, default);
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.PdfBytes.Take(5).Should().Equal("%PDF-"u8.ToArray());
        result.Value.SignatureAnchors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ValidResponse_AuthenticatesAndComputesHashAndAnchorsLocally()
    {
        var renderer = Create(async (request, ct) =>
        {
            request.RequestUri!.AbsoluteUri.Should().Be("http://renderer.railway.internal:8080/render");
            request.Headers.Authorization.Should().Be(new AuthenticationHeaderValue("Bearer", Token));
            request.Content!.Headers.ContentType!.MediaType.Should().Be("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
            (await request.Content.ReadAsByteArrayAsync(ct)).Should().Equal(1, 2, 3);
            return Archive("%PDF-example", Bounds);
        });
        var result = await renderer.RenderPreviewAsync([1, 2, 3], default);
        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.Value.SignatureAnchors.Should().ContainSingle();
        result.Value.PdfSha256.Should().HaveLength(64);
        result.Value.SupportsFinalSignatureAnchors.Should().BeTrue();
    }

    [Theory]
    [InlineData("http://public.example/render", Token)]
    [InlineData("http://renderer.railway.internal.evil.test/render", Token)]
    [InlineData("https://user:password@example.test/render", Token)]
    [InlineData("https://example.test/render?token=secret", Token)]
    [InlineData("https://example.test/render", "")]
    [InlineData("https://example.test/render", "short")]
    public async Task InvalidConfiguration_FailsBeforeSendingCredentials(string endpoint, string token)
    {
        var renderer = Create((_, _) => throw new Exception("Must not send"), endpoint, token);
        (await renderer.RenderPreviewAsync([1], default)).ErrorCode.Should().Be(ContractTemplateDocumentErrors.RendererUnavailable);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(302)]
    public async Task UnavailableWorker_DoesNotExposeResponseBodyOrFallback(int status)
    {
        var renderer = Create((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("sensitive diagnostics") }));
        var result = await renderer.RenderPreviewAsync([1], default);
        result.ErrorCode.Should().Be(ContractTemplateDocumentErrors.RendererUnavailable);
        result.ErrorMessage.Should().NotContain("sensitive");
    }

    [Theory]
    [InlineData("not a PDF", Bounds)]
    [InlineData("%PDF-example", "<html/>")]
    [InlineData("%PDF-example", "malformed")]
    public async Task InvalidOutput_IsRejected(string pdf, string bounds)
    {
        var renderer = Create((_, _) => Task.FromResult(Archive(pdf, bounds)));
        (await renderer.RenderPreviewAsync([1], default)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task DuplicateOrUnexpectedArchiveEntries_AreRejected()
    {
        var response = Archive("%PDF-example", Bounds, duplicate: true);
        (await Create((_, _) => Task.FromResult(response)).RenderPreviewAsync([1], default)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task OversizedResponse_IsRejectedBeforeReading()
    {
        var response = Archive("%PDF-example", Bounds);
        response.Content.Headers.ContentLength = 100 * 1024 * 1024;
        (await Create((_, _) => Task.FromResult(response)).RenderPreviewAsync([1], default)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task WorkerTimeout_FailsClosed()
    {
        var renderer = Create(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Archive("%PDF-example", Bounds); }, timeout: 1);
        (await renderer.RenderPreviewAsync([1], default)).ErrorCode.Should().Be(ContractTemplateDocumentErrors.RendererUnavailable);
    }

    [Fact]
    public async Task CallerCancellation_IsPropagated()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var renderer = Create((_, _) => throw new Exception("Must not send"));
        var act = () => renderer.RenderPreviewAsync([1], cancelled.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static RemoteContractTemplateRenderer Create(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        string endpoint = "http://renderer.railway.internal:8080/render", string token = Token, int timeout = 60) =>
        new(new HttpClient(new Handler(respond)), Options.Create(new ContractTemplateRendererOptions
        { RemoteEndpoint = endpoint, RemoteApiKey = token, TimeoutSeconds = timeout }));

    private static HttpResponseMessage Archive(string pdf, string bounds, bool duplicate = false)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("preview.pdf").Open())) writer.Write(pdf);
            using (var writer = new StreamWriter(zip.CreateEntry("bounds.html").Open())) writer.Write(bounds);
            if (duplicate) { using var writer = new StreamWriter(zip.CreateEntry("preview.pdf").Open()); writer.Write(pdf); }
        }
        var content = new ByteArrayContent(bytes.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return new(HttpStatusCode.OK) { Content = content };
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}

internal sealed class RemoteRendererFactAttribute : FactAttribute
{
    public RemoteRendererFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TRIPMATE_RENDERER_REMOTE_ENDPOINT"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TRIPMATE_RENDERER_REMOTE_KEY")))
            Skip = "Set TRIPMATE_RENDERER_REMOTE_ENDPOINT and TRIPMATE_RENDERER_REMOTE_KEY to verify the real private worker.";
    }
}