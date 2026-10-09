using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

using TripMate.Application.Common.Models;
using TripMate.Application.Features.TripReviews.Common;
using TripMate.Domain.Enums;

namespace TripMate.Api.Controllers.V1.Requests;

public sealed class SubmitTripReviewMetadataRequest
{
    public required int OverallRating { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public IReadOnlyList<SubmitTripReviewPoiRequest> PoiRatings { get; init; } = [];
    public string? RoutePacing { get; init; }
    public int? CspRating { get; init; }
    public bool PublishDisplayName { get; init; }
}

public sealed record SubmitTripReviewPoiRequest(long PoiId, int Rating);

public sealed class EditTripReviewRequest
{
    public required int OverallRating { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public required bool PublishDisplayName { get; init; }
    public required string Version { get; init; }
}

internal sealed record ParsedSubmitTripReviewRequest(
    int OverallRating,
    string Title,
    string Content,
    IReadOnlyList<TripReviewPoiInput> PoiRatings,
    IReadOnlyList<TripReviewPhotoInput> Photos,
    RoutePacingFeedback? RoutePacing,
    int? CspRating,
    bool PublishDisplayName);

internal static class TripReviewRequestReader
{
    public const string MetadataPartName = "metadata";
    public const string FilesPartName = "files";
    public const int MaximumMetadataUtf8Bytes = 64_000;
    public const long MaximumPostBodyBytes = 26_000_000;

    private static readonly HashSet<string> SubmitMembers =
    [
        nameof(SubmitTripReviewMetadataRequest.OverallRating).ToCamelCase(),
        nameof(SubmitTripReviewMetadataRequest.Title).ToCamelCase(),
        nameof(SubmitTripReviewMetadataRequest.Content).ToCamelCase(),
        nameof(SubmitTripReviewMetadataRequest.PoiRatings).ToCamelCase(),
        nameof(SubmitTripReviewMetadataRequest.RoutePacing).ToCamelCase(),
        nameof(SubmitTripReviewMetadataRequest.CspRating).ToCamelCase(),
        nameof(SubmitTripReviewMetadataRequest.PublishDisplayName).ToCamelCase(),
    ];

    private static readonly HashSet<string> RequiredSubmitMembers =
    [
        nameof(SubmitTripReviewMetadataRequest.OverallRating).ToCamelCase(),
        nameof(SubmitTripReviewMetadataRequest.Title).ToCamelCase(),
        nameof(SubmitTripReviewMetadataRequest.Content).ToCamelCase(),
    ];

    private static readonly HashSet<string> EditMembers =
    [
        nameof(EditTripReviewRequest.OverallRating).ToCamelCase(),
        nameof(EditTripReviewRequest.Title).ToCamelCase(),
        nameof(EditTripReviewRequest.Content).ToCamelCase(),
        nameof(EditTripReviewRequest.PublishDisplayName).ToCamelCase(),
        nameof(EditTripReviewRequest.Version).ToCamelCase(),
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<Result<ParsedSubmitTripReviewRequest>> ReadSubmitAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!HasMediaType(request, "multipart/form-data"))
            return Failure<ParsedSubmitTripReviewRequest>(TripReviewErrorCodes.UnsupportedMediaType,
                "Content-Type must be multipart/form-data.");
        if (request.ContentLength is > MaximumPostBodyBytes)
            return TooLarge<ParsedSubmitTripReviewRequest>();

        var mediaType = MediaTypeHeaderValue.Parse(request.ContentType!);
        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            return Failure<ParsedSubmitTripReviewRequest>(TripReviewErrorCodes.InvalidInput,
                "The multipart boundary is invalid.");
        }

        var originalBody = request.Body;
        var boundedBody = new MaximumLengthReadStream(originalBody, MaximumPostBodyBytes);
        request.Body = boundedBody;
        try
        {
            var reader = new MultipartReader(boundary, boundedBody)
            {
                BodyLengthLimit = MaximumPostBodyBytes,
            };
            string? metadataJson = null;
            var metadataCount = 0;
            var fileCount = 0;
            var invalidShape = false;
            var photos = new List<TripReviewPhotoInput>(TripReviewInputRules.MaximumPhotos);

            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(
                        section.ContentDisposition,
                        out var disposition)
                    || !string.Equals(disposition.DispositionType.Value,
                        "form-data",
                        StringComparison.OrdinalIgnoreCase))
                {
                    invalidShape = true;
                    await DrainAsync(section.Body, cancellationToken);
                    continue;
                }

                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                if (disposition.IsFileDisposition())
                {
                    fileCount++;
                    var bytes = await ReadBoundedAsync(
                        section.Body,
                        TripReviewInputRules.MaximumPhotoBytes,
                        cancellationToken);
                    if (!string.Equals(name, FilesPartName, StringComparison.Ordinal)
                        || fileCount > TripReviewInputRules.MaximumPhotos)
                    {
                        invalidShape = true;
                        continue;
                    }

                    var fileName = HeaderUtilities.RemoveQuotes(
                        disposition.FileNameStar.HasValue
                            ? disposition.FileNameStar
                            : disposition.FileName).Value ?? string.Empty;
                    photos.Add(new(
                        fileName,
                        section.ContentType ?? string.Empty,
                        bytes.LongLength,
                        bytes));
                    continue;
                }

                if (!disposition.IsFormDisposition()
                    || !string.Equals(name, MetadataPartName, StringComparison.Ordinal))
                {
                    invalidShape = true;
                    await DrainAsync(section.Body, cancellationToken);
                    continue;
                }

                metadataCount++;
                var metadataBytes = await ReadBoundedAsync(
                    section.Body,
                    MaximumMetadataUtf8Bytes,
                    cancellationToken);
                if (metadataCount != 1 || !IsJsonUtf8Part(section.ContentType))
                {
                    invalidShape = true;
                    continue;
                }

                try
                {
                    metadataJson = StrictUtf8.GetString(metadataBytes);
                }
                catch (DecoderFallbackException)
                {
                    invalidShape = true;
                }
            }

            // Multipart parsers may stop at the closing boundary. Draining the
            // endpoint-scoped counting stream ensures chunked epilogues still
            // participate in the 26,000,000-byte total request limit.
            await DrainAsync(boundedBody, cancellationToken);

            if (invalidShape || metadataCount != 1 || metadataJson is null)
            {
                return Failure<ParsedSubmitTripReviewRequest>(TripReviewErrorCodes.InvalidInput,
                    "The multipart request shape is invalid.");
            }

            var metadata = ParseSubmitMetadata(metadataJson);
            if (metadata.IsFailure)
            {
                return Result.Failure<ParsedSubmitTripReviewRequest>(
                    metadata.ErrorCode!,
                    metadata.ErrorMessage!);
            }

            var value = metadata.Value;
            return Result.Success(new ParsedSubmitTripReviewRequest(
                value.OverallRating,
                value.Title,
                value.Content,
                value.PoiRatings!.Select(item => new TripReviewPoiInput(item.PoiId, item.Rating)).ToArray(),
                photos,
                ParsePacing(value.RoutePacing),
                value.CspRating,
                value.PublishDisplayName));
        }
        catch (MaximumLengthExceededException)
        {
            return TooLarge<ParsedSubmitTripReviewRequest>();
        }
        catch (InvalidDataException)
        {
            return Failure<ParsedSubmitTripReviewRequest>(TripReviewErrorCodes.InvalidInput,
                "The multipart request is invalid.");
        }
        finally
        {
            request.Body = originalBody;
        }
    }

    public static async Task<Result<EditTripReviewRequest>> ReadEditAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!HasMediaType(request, "application/json"))
            return Failure<EditTripReviewRequest>(TripReviewErrorCodes.UnsupportedMediaType,
                "Content-Type must be application/json.");

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return InvalidInput<EditTripReviewRequest>();
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return InvalidInput<EditTripReviewRequest>();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!EditMembers.Contains(property.Name))
                    return Failure<EditTripReviewRequest>(TripReviewErrorCodes.InvalidEditPayload,
                        "The edit payload contains an unsupported member.");
                if (!seen.Add(property.Name))
                    return InvalidInput<EditTripReviewRequest>();
            }

            if (!seen.SetEquals(EditMembers))
                return InvalidInput<EditTripReviewRequest>();

            try
            {
                var value = document.RootElement.Deserialize<EditTripReviewRequest>(SerializerOptions);
                return value is null || value.Title is null || value.Content is null || value.Version is null
                    ? InvalidInput<EditTripReviewRequest>()
                    : Result.Success(value);
            }
            catch (JsonException)
            {
                return InvalidInput<EditTripReviewRequest>();
            }
        }
    }

    private static Result<SubmitTripReviewMetadataRequest> ParseSubmitMetadata(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return InvalidInput<SubmitTripReviewMetadataRequest>();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!SubmitMembers.Contains(property.Name) || !seen.Add(property.Name))
                    return InvalidInput<SubmitTripReviewMetadataRequest>();
            }
            if (!RequiredSubmitMembers.IsSubsetOf(seen))
                return InvalidInput<SubmitTripReviewMetadataRequest>();

            var value = document.RootElement.Deserialize<SubmitTripReviewMetadataRequest>(SerializerOptions);
            if (value is null || value.Title is null || value.Content is null || value.PoiRatings is null
                || !IsPacing(value.RoutePacing))
            {
                return InvalidInput<SubmitTripReviewMetadataRequest>();
            }
            return Result.Success(value);
        }
        catch (JsonException)
        {
            return InvalidInput<SubmitTripReviewMetadataRequest>();
        }
    }

    private static bool HasMediaType(HttpRequest request, string expected)
    {
        return MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            && string.Equals(mediaType.MediaType.Value, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJsonUtf8Part(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
            || !string.Equals(mediaType.MediaType.Value,
                "application/json",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !mediaType.Charset.HasValue
            || string.Equals(HeaderUtilities.RemoveQuotes(mediaType.Charset).Value,
                "utf-8",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPacing(string? value) => value is null
        or TripReviewContextValues.TooTight
        or TripReviewContextValues.WellPaced
        or TripReviewContextValues.TooLoose;

    private static RoutePacingFeedback? ParsePacing(string? value) => value switch
    {
        null => null,
        TripReviewContextValues.TooTight => RoutePacingFeedback.TooTight,
        TripReviewContextValues.WellPaced => RoutePacingFeedback.WellPaced,
        TripReviewContextValues.TooLoose => RoutePacingFeedback.TooLoose,
        _ => throw new InvalidOperationException("Pacing was validated before conversion."),
    };

    private static async Task<byte[]> ReadBoundedAsync(
        Stream input,
        long maximumLength,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var remaining = maximumLength + 1 - output.Length;
            var read = await input.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read == 0)
                return output.ToArray();
            output.Write(buffer, 0, read);
            if (output.Length > maximumLength)
                throw new MaximumLengthExceededException();
        }
    }

    private static async Task DrainAsync(Stream input, CancellationToken cancellationToken)
    {
        var buffer = new byte[81_920];
        while (await input.ReadAsync(buffer, cancellationToken) != 0)
        {
        }
    }

    private static Result<T> InvalidInput<T>() =>
        Failure<T>(TripReviewErrorCodes.InvalidInput, "The review request is invalid.");

    private static Result<T> TooLarge<T>() =>
        Failure<T>(TripReviewErrorCodes.BodyTooLarge, "The request body is too large.");

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(code, message);

    private static string ToCamelCase(this string value) =>
        JsonNamingPolicy.CamelCase.ConvertName(value);

    private sealed class MaximumLengthExceededException : IOException;

    private sealed class MaximumLengthReadStream(Stream inner, long maximumLength) : Stream
    {
        private long _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var result = inner.Read(buffer, offset, count);
            Count(result);
            return result;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = await inner.ReadAsync(buffer, cancellationToken);
            Count(result);
            return result;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        private void Count(int count)
        {
            _read += count;
            if (_read > maximumLength)
                throw new MaximumLengthExceededException();
        }
    }
}