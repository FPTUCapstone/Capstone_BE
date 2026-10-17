using System.Reflection;

using FluentValidation;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using TripMate.Application.Common.Behaviours;
using TripMate.Application.Features.Traveler.Bookings.Expiry;

namespace TripMate.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        services.AddValidatorsFromAssembly(assembly);
        services.AddScoped<PendingBookingExpiryProcessor>();

        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(assembly);
            cfg.AddOpenBehavior(typeof(ValidationBehaviour<,>));
            cfg.AddOpenBehavior(typeof(Features.Admin.AuditLogs.Common.AuditFailureBehaviour<,>));
        });

        return services;
    }
}