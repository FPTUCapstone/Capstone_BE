using System.Net;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using TripMate.Api.IntegrationTests.Infrastructure;
using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Entities;

namespace TripMate.Api.IntegrationTests.Tours;

public sealed class TourCategoriesEndpointTests
{
    [Fact]
    public async Task Categories_AnonymousEmptyTaxonomyIsEmptyAndNoStore()
    {
        await using var factory = new TripMateApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/tour-categories");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await response.Content.ReadAsStringAsync()).Should().Be("[]");
    }

    [Fact]
    public async Task Categories_AnonymousActiveOnlyAndNameCodeOrder()
    {
        await using var factory = new TripMateApiFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var inactive = TourCategory.Create("hidden", "A hidden fixture");
            context.TourCategories.AddRange(TourCategory.Create("z", "Beach"), TourCategory.Create("a", "Beach"),
                TourCategory.Create("c", "Adventure"), inactive);
            ((Microsoft.EntityFrameworkCore.DbContext)context).Entry(inactive).Property(c => c.IsActive).CurrentValue = false;
            await context.SaveChangesAsync(CancellationToken.None);
        }
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/tour-categories");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.EnumerateArray().Select(row => row.GetProperty("code").GetString())
            .Should().Equal("c", "a", "z");
        body.RootElement[0].EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("categoryId", "code", "name");
    }
}