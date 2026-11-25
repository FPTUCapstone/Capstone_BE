using Microsoft.Extensions.Options;

namespace TripMate.Application.Features.Rerouting.Common;

public sealed class ReroutingOptionsValidator : IValidateOptions<ReroutingOptions>
{
    public ValidateOptionsResult Validate(string? name, ReroutingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.ProposalTtl < ReroutingOptions.MinProposalTtl || options.ProposalTtl > ReroutingOptions.MaxProposalTtl)
        {
            failures.Add($"{nameof(options.ProposalTtl)} must be between 1 and 60 minutes.");
        }

        if (options.ProposalRetryWindow < ReroutingOptions.MinRetryWindow
            || options.ProposalRetryWindow > ReroutingOptions.MaxRetryWindow)
        {
            failures.Add($"{nameof(options.ProposalRetryWindow)} must be between 1 and 240 minutes.");
        }

        if (options.MaxVariants is < ReroutingOptions.MinMaxVariants or > ReroutingOptions.MaxMaxVariants)
        {
            failures.Add(
                $"{nameof(options.MaxVariants)} must be between {ReroutingOptions.MinMaxVariants} and {ReroutingOptions.MaxMaxVariants}.");
        }

        if (options.MaxReplacementsPerStop is < ReroutingOptions.MinMaxReplacementsPerStop
            or > ReroutingOptions.MaxMaxReplacementsPerStop)
        {
            failures.Add(
                $"{nameof(options.MaxReplacementsPerStop)} must be between {ReroutingOptions.MinMaxReplacementsPerStop} and {ReroutingOptions.MaxMaxReplacementsPerStop}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}