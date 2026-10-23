using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace TripMate.Api.Common;

/// <summary>
/// Keeps MVC from eagerly parsing multipart input before a feature-scoped bounded reader.
/// Authentication/authorization still runs before this resource filter.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        Remove<FormValueProviderFactory>(context.ValueProviderFactories);
        Remove<FormFileValueProviderFactory>(context.ValueProviderFactories);
        Remove<JQueryFormValueProviderFactory>(context.ValueProviderFactories);
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }

    private static void Remove<T>(IList<IValueProviderFactory> factories)
        where T : IValueProviderFactory
    {
        var factory = factories.OfType<T>().FirstOrDefault();
        if (factory is not null)
            factories.Remove(factory);
    }
}