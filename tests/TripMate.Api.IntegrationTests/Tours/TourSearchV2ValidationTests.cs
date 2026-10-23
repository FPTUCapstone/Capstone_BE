using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Tours.Search.V2;
using TripMate.Domain.Entities;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed class TourSearchV2ValidationTests
{
    [Fact]
    public async Task Validator_ExistingMiddlewareProducesFieldLinkedProblemDetails()
    {
        await using var factory = new TripMateApiFactory();
        using var scope = factory.Services.CreateScope();
        var validator = new TourSearchV2RequestValidator(scope.ServiceProvider.GetRequiredService<IApplicationDbContext>());
        var validation = await validator.ValidateAsync(new TourSearchV2Request(MinPrice: 2, MaxPrice: 1));
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var options = new Microsoft.AspNetCore.Mvc.JsonOptions();
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        var middleware = new TripMate.Api.Middleware.ExceptionHandlingMiddleware(
            _ => throw new TripMate.Application.Common.Exceptions.ValidationException(validation.Errors),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TripMate.Api.Middleware.ExceptionHandlingMiddleware>.Instance,
            Microsoft.Extensions.Options.Options.Create(options));
        await middleware.InvokeAsync(context);
        context.Response.StatusCode.Should().Be(400);
        context.Response.Body.Position = 0;
        using var body = await System.Text.Json.JsonDocument.ParseAsync(context.Response.Body);
        body.RootElement.GetProperty("errorCode").GetString().Should().Be("MSG157");
        body.RootElement.GetProperty("errors").GetProperty("maxPrice")[0].GetString()
            .Should().Be(TourSearchV2Request.InvalidPriceRangeMessage);
    }

    [Fact]
    public async Task Validator_MapsRangeMessagesToFields()
    {
        await using var factory = new TripMateApiFactory();
        using var scope = factory.Services.CreateScope();
        var validator = new TourSearchV2RequestValidator(scope.ServiceProvider.GetRequiredService<IApplicationDbContext>());
        var result = await validator.ValidateAsync(new TourSearchV2Request(
            StartDate: new DateOnly(2026, 10, 16), EndDate: new DateOnly(2026, 10, 15), MinPrice: 2, MaxPrice: 1));
        result.Errors.Should().Contain(e => e.PropertyName == "EndDate" && e.ErrorCode == "MSG158"
            && e.ErrorMessage == "Start date must not be later than end date.");
        result.Errors.Should().Contain(e => e.PropertyName == "MaxPrice" && e.ErrorCode == "MSG157"
            && e.ErrorMessage == "Minimum price must not be greater than maximum price.");
    }

    [Fact]
    public async Task Validator_AcceptsSingleDatesAndTechnicalBoundaries()
    {
        await using var factory = new TripMateApiFactory();
        using var scope = factory.Services.CreateScope();
        var validator = new TourSearchV2RequestValidator(scope.ServiceProvider.GetRequiredService<IApplicationDbContext>());
        foreach (var request in new[]
        {
            new TourSearchV2Request(StartDate: new DateOnly(2020, 1, 1)),
            new TourSearchV2Request(EndDate: new DateOnly(2020, 1, 1)),
            new TourSearchV2Request(MinPrice: 0, MaxPrice: 9_999_999_999, DurationDays: int.MaxValue, MinimumRating: 1),
            new TourSearchV2Request(MinimumRating: 5, PageSize: 100),
        })
            (await validator.ValidateAsync(request)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validator_RejectsAllOutOfRangeValues()
    {
        await using var factory = new TripMateApiFactory();
        using var scope = factory.Services.CreateScope();
        var validator = new TourSearchV2RequestValidator(scope.ServiceProvider.GetRequiredService<IApplicationDbContext>());
        var result = await validator.ValidateAsync(new TourSearchV2Request(
            MinPrice: -1, MaxPrice: 10_000_000_000, DurationDays: 0, MinimumRating: 5.01m,
            Sort: "PRICEASC", Page: 0, PageSize: 101));
        result.Errors.Select(e => e.PropertyName).Should().Contain([
            "MinPrice", "MaxPrice", "DurationDays", "MinimumRating", "Sort", "Page", "PageSize"]);
    }

    [Fact]
    public async Task Validator_UsesActiveServerOwnedCategory()
    {
        await using var factory = new TripMateApiFactory();
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        context.TourCategories.Add(TourCategory.Create("fixture", "Test category"));
        var inactive = TourCategory.Create("inactive", "Inactive test category");
        context.TourCategories.Add(inactive);
        ((Microsoft.EntityFrameworkCore.DbContext)context).Entry(inactive).Property(category => category.IsActive).CurrentValue = false;
        await context.SaveChangesAsync(CancellationToken.None);
        var validator = new TourSearchV2RequestValidator(context);
        (await validator.ValidateAsync(new TourSearchV2Request(Category: "fixture"))).IsValid.Should().BeTrue();
        (await validator.ValidateAsync(new TourSearchV2Request(Category: "missing"))).Errors
            .Should().Contain(e => e.PropertyName == "Category");
        (await validator.ValidateAsync(new TourSearchV2Request(Category: "inactive"))).Errors
            .Should().Contain(e => e.PropertyName == "Category");
    }
}