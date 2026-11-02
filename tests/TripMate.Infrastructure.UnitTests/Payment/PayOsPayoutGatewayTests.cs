using System.Net;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using TripMate.Application.Features.Admin.PayoutSettlements.Processing;
using TripMate.Infrastructure.Services.Payment.PayoutDisbursement;

namespace TripMate.Infrastructure.UnitTests.Payment;

/// <summary>
/// Never reaches the network: every test uses a stub <see cref="HttpMessageHandler"/>. The wire
/// shapes follow the PayOS Payouts documentation and the official PayOS .NET SDK.
/// </summary>
public sealed class PayOsPayoutGatewayTests
{
    private const string ClientId = "payout-client-id";
    private const string ApiKey = "payout-api-key-SECRET";
    private const string ChecksumKey = "checksum-key";

    // Independently computed (Python hmac/hashlib) from the SDK's algorithm: keys sorted, values
    // URL-encoded, "category" present and empty, HMAC-SHA256 with the checksum key, lowercase hex.
    private const string KnownSignedData =
        "amount=10800000&category=&description=TripMate%20Payout%20PO-000001&referenceId=PO-000001&toAccountNumber=1029384756&toBin=970436";
    private const string KnownSignature = "5c075cbf5aac3d0cd1a456502887a12ed9f2262d18a358e8dcfd599aa27d01fd";

    [Fact]
    public void Signature_MatchesTheIndependentlyComputedVectors()
    {
        PayOsPayoutSignature.SignedData(Fields("PO-000001", 10_800_000, "TripMate Payout PO-000001", "970436", "1029384756"))
            .Should().Be(KnownSignedData);
        PayOsPayoutSignature.Compute(ChecksumKey, Fields("PO-000001", 10_800_000, "TripMate Payout PO-000001", "970436", "1029384756"))
            .Should().Be(KnownSignature);
        PayOsPayoutSignature.Compute("another/key+1", Fields("PO-000002", 2_500_000, "TripMate Payout PO-000002", "970415", "0123456789"))
            .Should().Be("622c13be5fba13760e3564a4793631a97ecc611786d9694d1b830e34141be021");
    }

    [Fact]
    public void IdempotencyHeader_IsADeterministicUuidDerivedFromTheTeamKey()
    {
        var first = PayOsPayoutSignature.IdempotencyHeaderValue("tripmate-payout-1");

        Guid.TryParse(first, out _).Should().BeTrue("the provider documents a format check on the key");
        PayOsPayoutSignature.IdempotencyHeaderValue("tripmate-payout-1").Should().Be(first);
        PayOsPayoutSignature.IdempotencyHeaderValue("tripmate-payout-2").Should().NotBe(first);
    }

    [Fact]
    public async Task Submit_SendsTheDocumentedRequestWithAValidSignatureAndAcceptsTheResponse()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var gateway = Create(request =>
        {
            captured = request;
            body = request.Content!.ReadAsStringAsync().Result;
            return Json(HttpStatusCode.OK, CreatedResponse("payos-1", "PROCESSING", "PROCESSING"));
        });

        var result = await gateway.SubmitAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(PayoutSubmitOutcome.Accepted);
        result.GatewayPayoutId.Should().Be("payos-1");
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.ToString().Should().Be("https://api-merchant.payos.vn/v1/payouts");
        captured.Headers.GetValues("x-client-id").Should().Equal(ClientId);
        captured.Headers.GetValues("x-api-key").Should().Equal(ApiKey);
        captured.Headers.GetValues("x-signature").Should().Equal(KnownSignature);
        Guid.TryParse(captured.Headers.GetValues("x-idempotency-key").Single(), out _).Should().BeTrue();

        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        root.GetProperty("referenceId").GetString().Should().Be("PO-000001");
        root.GetProperty("amount").GetInt64().Should().Be(10_800_000);
        root.GetProperty("amount").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("description").GetString().Should().Be("TripMate Payout PO-000001");
        root.GetProperty("toBin").GetString().Should().Be("970436");
        root.GetProperty("toAccountNumber").GetString().Should().Be("1029384756");
        root.GetProperty("category").ValueKind.Should().Be(JsonValueKind.Null, "the signed data includes an empty category exactly as the SDK sends it");
        root.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "referenceId", "amount", "description", "toBin", "toAccountNumber", "category");
    }

    [Fact]
    public async Task Submit_SanitizesTheDescriptionBeforeSendingAndSigning()
    {
        string? body = null;
        var gateway = Create(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return Json(HttpStatusCode.OK, CreatedResponse("payos-1", "PROCESSING", "PROCESSING"));
        });

        await gateway.SubmitAsync(Request() with { Description = "Thanh toán Đợt tháng 10 cho nhà điều hành" }, CancellationToken.None);

        using var json = JsonDocument.Parse(body!);
        var description = json.RootElement.GetProperty("description").GetString()!;
        description.Should().MatchRegex("^[ -~]+$").And.HaveLength(PayoutDescription.MaxLength);
    }

    [Fact]
    public async Task Submit_SuccessWithoutAPayoutId_IsTransientBecauseTheTransferMayExist()
    {
        var gateway = Create(_ => Json(HttpStatusCode.OK, """{"code":"00","desc":"success","data":{}}"""));

        var result = await gateway.SubmitAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(PayoutSubmitOutcome.Transient);
        result.FailureCode.Should().Be("gateway_malformed_response");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"code":"20","desc":"Số dư không đủ"}""", "gateway_insufficient_balance")]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"20","desc":"Số dư không đủ"}""", "gateway_insufficient_balance")]
    [InlineData(HttpStatusCode.BadRequest, """{"desc":"Tài khoản đích không hợp lệ"}""", "gateway_invalid_account")]
    [InlineData(HttpStatusCode.BadRequest, """{"desc":"Tài khoản nhận không tìm thấy"}""", "gateway_invalid_account")]
    [InlineData(HttpStatusCode.BadRequest, """{"desc":"Mã kiểm tra(signature) không hợp lệ"}""", "gateway_invalid_signature")]
    [InlineData(HttpStatusCode.BadRequest, """{"desc":"something else entirely"}""", "gateway_rejected")]
    [InlineData(HttpStatusCode.OK, """{"code":"99","desc":"unknown failure"}""", "gateway_rejected")]
    [InlineData(HttpStatusCode.Unauthorized, "{}", "gateway_unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "{}", "gateway_unauthorized")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "not json at all", "gateway_rejected")]
    public async Task Submit_DefinitiveFailures_AreRejectionsWithASafeCode(HttpStatusCode status, string body, string expectedCode)
    {
        var gateway = Create(_ => Json(status, body));

        var result = await gateway.SubmitAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(PayoutSubmitOutcome.DefinitiveRejection);
        result.FailureCode.Should().Be(expectedCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"code":"20","desc":"Idempotency key đã tồn tại"}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"desc":"Idempotency key đã tồn tại"}""")]
    [InlineData(HttpStatusCode.Conflict, """{"desc":"idempotency key already exists"}""")]
    public async Task Submit_IdempotencyKeyAlreadyUsed_IsReportedSoTheCallerLooksTheTransferUp(HttpStatusCode status, string body)
    {
        var gateway = Create(_ => Json(status, body));

        var result = await gateway.SubmitAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(PayoutSubmitOutcome.IdempotencyKeyExists);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData((HttpStatusCode)429)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Submit_TimeoutRateLimitAndServerErrors_AreTransient(HttpStatusCode status)
    {
        var gateway = Create(_ => Json(status, """{"desc":"try later"}"""));

        var result = await gateway.SubmitAsync(Request(), CancellationToken.None);

        result.Outcome.Should().Be(PayoutSubmitOutcome.Transient);
        result.FailureCode.Should().Be("gateway_unavailable");
    }

    [Fact]
    public async Task Submit_NetworkFailureAndTimeout_AreTransient()
    {
        var network = Create(_ => throw new HttpRequestException("connection reset"));
        var timeout = Create(_ => throw new TaskCanceledException("timed out", new TimeoutException()));

        (await network.SubmitAsync(Request(), CancellationToken.None)).Outcome.Should().Be(PayoutSubmitOutcome.Transient);
        (await timeout.SubmitAsync(Request(), CancellationToken.None)).Outcome.Should().Be(PayoutSubmitOutcome.Transient);
    }

    [Fact]
    public async Task Submit_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var gateway = Create(_ => throw new TaskCanceledException("cancelled"));

        var act = async () => await gateway.SubmitAsync(Request(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Submit_Failures_NeverEchoTheResponseBodyOrCredentials()
    {
        var gateway = Create(_ => Json(HttpStatusCode.BadRequest, $$"""{"desc":"leaked {{ApiKey}} 1029384756"}"""));

        var result = await gateway.SubmitAsync(Request(), CancellationToken.None);

        result.FailureCode.Should().Be("gateway_rejected");
        result.ToString().Should().NotContain(ApiKey).And.NotContain("1029384756").And.NotContain(ChecksumKey);
    }

    [Theory]
    [InlineData("PROCESSING", "PROCESSING", PayoutGatewayState.Processing)]
    [InlineData("SUCCEEDED", "PROCESSING", PayoutGatewayState.Succeeded)]
    [InlineData("SUCCEEDED", "COMPLETED", PayoutGatewayState.Succeeded)]
    [InlineData("FAILED", "PROCESSING", PayoutGatewayState.Failed)]
    [InlineData("CANCELLED", "PROCESSING", PayoutGatewayState.Cancelled)]
    [InlineData("RECEIVED", "PROCESSING", PayoutGatewayState.Unknown)]
    [InlineData("ON_HOLD", "PROCESSING", PayoutGatewayState.Unknown)]
    [InlineData("REJECTED", "PROCESSING", PayoutGatewayState.Rejected)]
    [InlineData("REVERSED", "PROCESSING", PayoutGatewayState.Reversed)]
    [InlineData("reversed", "PROCESSING", PayoutGatewayState.Reversed)]
    [InlineData("SOMETHING_NEW", "PROCESSING", PayoutGatewayState.Unknown)]
    public async Task Status_MapsTheTransactionStateAndTreatsEverythingUnconfirmedAsUnknown(
        string transactionState, string approvalState, PayoutGatewayState expected)
    {
        var gateway = Create(_ => Json(HttpStatusCode.OK, CreatedResponse("payos-1", transactionState, approvalState)));

        var result = await gateway.GetStatusAsync("payos-1", CancellationToken.None);

        result.IsAvailable.Should().BeTrue();
        result.State.Should().Be(expected);
    }

    [Fact]
    public async Task Status_AcceptsTransactionsAsAnArrayAndAsAnObjectKeyedByIndex()
    {
        var asArray = Create(_ => Json(HttpStatusCode.OK, """
            {"code":"00","desc":"success","data":{"id":"p1","referenceId":"PO-1","approvalState":"PROCESSING",
             "transactions":[{"id":"t1","state":"SUCCEEDED"}]}}
            """));
        var asObject = Create(_ => Json(HttpStatusCode.OK, """
            {"code":"00","desc":"success","data":{"id":"p1","referenceId":"PO-1","approvalState":"PROCESSING",
             "transactions":{"0":{"id":"t1","state":"SUCCEEDED"}}}}
            """));

        (await asArray.GetStatusAsync("p1", CancellationToken.None)).State.Should().Be(PayoutGatewayState.Succeeded);
        (await asObject.GetStatusAsync("p1", CancellationToken.None)).State.Should().Be(PayoutGatewayState.Succeeded);
    }

    [Fact]
    public async Task Status_WithSeveralTransactions_SucceedsOnlyWhenAllSucceeded()
    {
        var mixed = Create(_ => Json(HttpStatusCode.OK, """
            {"code":"00","data":{"id":"p1","approvalState":"PROCESSING",
             "transactions":[{"state":"SUCCEEDED"},{"state":"PROCESSING"}]}}
            """));
        var oneFailed = Create(_ => Json(HttpStatusCode.OK, """
            {"code":"00","data":{"id":"p1","approvalState":"PROCESSING",
             "transactions":[{"state":"SUCCEEDED"},{"state":"FAILED"}]}}
            """));

        (await mixed.GetStatusAsync("p1", CancellationToken.None)).State.Should().Be(PayoutGatewayState.Processing);
        (await oneFailed.GetStatusAsync("p1", CancellationToken.None)).State.Should().Be(PayoutGatewayState.Failed);
    }

    [Theory]
    [InlineData("FAILED", PayoutGatewayState.Failed)]
    [InlineData("CANCELLED", PayoutGatewayState.Cancelled)]
    [InlineData("REJECTED", PayoutGatewayState.Rejected)]
    [InlineData("REVERSED", PayoutGatewayState.Reversed)]
    [InlineData("COMPLETED", PayoutGatewayState.Unknown)]
    [InlineData("PROCESSING", PayoutGatewayState.Processing)]
    public async Task Status_WithoutTransactions_NeverReportsSuccessFromTheApprovalStateAlone(
        string approvalState, PayoutGatewayState expected)
    {
        var gateway = Create(_ => Json(HttpStatusCode.OK, $$$"""
            {"code":"00","data":{"id":"p1","approvalState":"{{{approvalState}}}","transactions":[]}}
            """));

        (await gateway.GetStatusAsync("p1", CancellationToken.None)).State.Should().Be(expected);
    }

    [Fact]
    public async Task Status_SendsAGetWithCredentialsOnlyAndEscapesTheId()
    {
        HttpRequestMessage? captured = null;
        var gateway = Create(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, CreatedResponse("a/b c", "PROCESSING", "PROCESSING"));
        });

        await gateway.GetStatusAsync("a/b c", CancellationToken.None);

        captured!.Method.Should().Be(HttpMethod.Get);
        captured.RequestUri!.AbsoluteUri.Should().Be("https://api-merchant.payos.vn/v1/payouts/a%2Fb%20c");
        captured.Headers.GetValues("x-client-id").Should().Equal(ClientId);
        captured.Headers.GetValues("x-api-key").Should().Equal(ApiKey);
        captured.Headers.Contains("x-signature").Should().BeFalse();
        captured.Headers.Contains("x-idempotency-key").Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"desc":"boom"}""")]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, """{"code":"20","desc":"bad"}""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, """{"code":"00"}""")]
    public async Task Status_AnyFailureOfTheCallItself_IsUnavailableNotAnOutcome(HttpStatusCode status, string body)
    {
        var gateway = Create(_ => Json(status, body));

        var result = await gateway.GetStatusAsync("p1", CancellationToken.None);

        result.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Status_NetworkFailure_IsUnavailable()
    {
        var gateway = Create(_ => throw new HttpRequestException("reset"));

        (await gateway.GetStatusAsync("p1", CancellationToken.None)).IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Lookup_ByReference_FindsTheMatchingPayoutAndItsState()
    {
        HttpRequestMessage? captured = null;
        var gateway = Create(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, """
                {"code":"00","data":{"pagination":{"total":1,"count":1,"hasMore":false},
                 "payouts":{"0":{"id":"p9","referenceId":"PO-000001","approvalState":"PROCESSING",
                  "transactions":{"0":{"id":"t","state":"SUCCEEDED"}}}}}}
                """);
        });

        var result = await gateway.FindByReferenceAsync("PO-000001", CancellationToken.None);

        result.Outcome.Should().Be(PayoutLookupOutcome.Found);
        result.GatewayPayoutId.Should().Be("p9");
        result.State.Should().Be(PayoutGatewayState.Succeeded);
        captured!.Method.Should().Be(HttpMethod.Get);
        captured.RequestUri!.AbsoluteUri.Should().Be("https://api-merchant.payos.vn/v1/payouts?referenceId=PO-000001");
        captured.Headers.Contains("x-signature").Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"code":"00","data":{"pagination":{"total":0,"count":0,"hasMore":false},"payouts":{}}}""")]
    [InlineData("""{"code":"00","data":{"pagination":{"total":0,"count":0,"hasMore":false},"payouts":[]}}""")]
    [InlineData("""{"code":"00","data":{"payouts":{"0":{"id":"other","referenceId":"PO-999999","transactions":[]}}}}""")]
    public async Task Lookup_AnEmptyOrUnrelatedList_MeansNothingWasCreated(string body)
    {
        var gateway = Create(_ => Json(HttpStatusCode.OK, body));

        var result = await gateway.FindByReferenceAsync("PO-000001", CancellationToken.None);

        result.Outcome.Should().Be(PayoutLookupOutcome.NotFound);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.OK, """{"code":"20","desc":"bad"}""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, """{"code":"00","data":{}}""")]
    [InlineData(HttpStatusCode.OK, """{"code":"00","data":null}""")]
    public async Task Lookup_WhenTheCallFailsOrTheShapeIsUnexpected_IsFailedNeverNotFound(HttpStatusCode status, string body)
    {
        var gateway = Create(_ => Json(status, body));

        (await gateway.FindByReferenceAsync("PO-000001", CancellationToken.None)).Outcome.Should().Be(PayoutLookupOutcome.Failed);
    }

    [Fact]
    public async Task Lookup_NetworkFailure_IsFailed()
    {
        var gateway = Create(_ => throw new HttpRequestException("reset"));

        (await gateway.FindByReferenceAsync("PO-000001", CancellationToken.None)).Outcome.Should().Be(PayoutLookupOutcome.Failed);
    }

    private static PayOsPayoutGateway Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var client = new HttpClient(new StubHandler(respond));
        var options = new PayoutDisbursementOptions
        {
            Provider = PayoutDisbursementOptions.PayOsProvider,
            PayOs = new PayOsPayoutOptions
            {
                LiveTransfersEnabled = true,
                ClientId = ClientId,
                ApiKey = ApiKey,
                ChecksumKey = ChecksumKey,
            },
        };
        return new PayOsPayoutGateway(client, options, NullLogger<PayOsPayoutGateway>.Instance);
    }

    private static PayoutSubmitRequest Request() => new(
        "PO-000001", 10_800_000m, "TripMate Payout PO-000001", "970436", "1029384756", "tripmate-payout-1");

    private static SortedDictionary<string, string> Fields(
        string reference, long amount, string description, string toBin, string toAccountNumber) => new(StringComparer.Ordinal)
        {
            ["referenceId"] = reference,
            ["amount"] = amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["description"] = description,
            ["toBin"] = toBin,
            ["toAccountNumber"] = toAccountNumber,
            ["category"] = string.Empty,
        };

    private static string CreatedResponse(string id, string transactionState, string approvalState) => """
        {"code":"00","desc":"success","data":{"id":"@ID@","referenceId":"PO-000001","approvalState":"@APPROVAL@",
         "transactions":{"0":{"id":"t1","referenceId":"PO-000001","amount":10800000,"state":"@STATE@"}}}}
        """
        .Replace("@ID@", id)
        .Replace("@APPROVAL@", approvalState)
        .Replace("@STATE@", transactionState);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}