using Microsoft.Extensions.Configuration;

using TripMate.Application.Features.Traveler.Payments.Common;
using TripMate.Infrastructure.Services.Payment;

if (args.Length != 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var webhookUrl))
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/TripMate.PayOsWebhookRegistrar -- <https-webhook-url>");
    return 2;
}

var configuration = new ConfigurationBuilder()
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var options = PaymentOptionsConfiguration.Read(configuration);

try
{
    using var httpClient = new HttpClient();
    var registrar = new PayOsWebhookRegistrar(httpClient, options);
    await registrar.ConfirmAsync(webhookUrl, CancellationToken.None);
    Console.WriteLine("PayOS webhook registration succeeded.");
    return 0;
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
catch (InvalidOperationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
catch (HttpRequestException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
