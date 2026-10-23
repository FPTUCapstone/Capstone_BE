using System.Reflection;

using FluentValidation;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using TripMate.Application.Common.Behaviours;
using TripMate.Application.Features.Operator.Revenue.Common;
using TripMate.Application.Features.Traveler.Bookings.Expiry;
using TripMate.Application.Features.TripAlerts.Common;
using TripMate.Application.Features.TripAlerts.Scanning;
using TripMate.Application.Features.TripAlerts.Weather;

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
        services.AddScoped<WeatherAlertClassifier>();
        services.AddScoped<TripAlertScanner>();

        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(assembly);
            cfg.AddOpenBehavior(typeof(ValidationBehaviour<,>));
            cfg.AddOpenBehavior(typeof(Features.Admin.AuditLogs.Common.AuditFailureBehaviour<,>));
        });

        return services;
    }
}