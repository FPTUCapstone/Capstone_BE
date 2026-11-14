using System.Reflection;

using FluentValidation;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using TripMate.Application.Common.Behaviours;
using TripMate.Application.Common.Interfaces;
using TripMate.Application.Features.Operator.Revenue.Common;
using TripMate.Application.Features.Tours.Search;
using TripMate.Application.Features.Traveler.Bookings.Expiry;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.TripAlerts.Scanning;
using TripMate.Application.Features.TripAlerts.Weather;
using TripMate.Application.Features.TourRecommendations.Refresh;
using TripMate.Application.Features.Weather.Locations;
using TripMate.Application.Features.Weather.Matching;

namespace TripMate.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        services.AddValidatorsFromAssembly(assembly);
        services.AddScoped<PendingBookingExpiryProcessor>();
        services.AddScoped<OperatorRevenueAggregator>();
        services.AddScoped<TripAlertPersistence>();
        services.AddScoped<TripAlertScanner>();
        services.AddScoped<ITourRecommendationCandidateReader, TourRecommendationCandidateReader>();

        // Weather alerts support Operator review; only Operator commands can cancel a tour.
        services.AddScoped<IWeatherDepartureImpactLoader, WeatherDepartureImpactLoader>();
        services.AddScoped<IWeatherAffectedDepartureFinder, WeatherAffectedDepartureFinder>();
        services.AddScoped<IWeatherLocationSyncService, WeatherLocationSyncService>();
        services.AddSingleton<IWeatherSafetyReviewNotifier, NullWeatherSafetyReviewNotifier>();

        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(assembly);
            cfg.AddOpenBehavior(typeof(ValidationBehaviour<,>));
            cfg.AddOpenBehavior(typeof(Features.Admin.AuditLogs.Common.AuditFailureBehaviour<,>));
        });

        return services;
    }
}
