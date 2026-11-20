using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Corfedo.Troupe.AotSmoke.Components;

// A component in a class library, as applications usually hold them. The library enables the configuration binding
// generator itself, because the SDK enables it only in the project that publishes.
public sealed class FeedComponent : IComponent
{
    public void Compose(IComponentBuilder component) =>
        component.Options<FeedOptions, FeedValidator>("Feed", (section, options) => section.Bind(options));
}

public sealed class FeedOptions
{
    public string Name { get; set; } = "";
    public RetryOptions Retry { get; set; } = new();
    public List<string> Tags { get; set; } = [];
}

public sealed class RetryOptions
{
    public int Count { get; set; }
}

public sealed class FeedValidator : IValidateOptions<FeedOptions>
{
    public ValidateOptionsResult Validate(string? name, FeedOptions options) =>
        options.Retry.Count > 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail("Feed:Retry:Count must be positive.");
}
