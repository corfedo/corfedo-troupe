using Microsoft.Extensions.Configuration;

namespace Corfedo.Troupe;

// A configuration provider without values that records the paths enumerated through the configuration while it records. An
// action that fails still counts by what it enumerated; its failure surfaces when the options are created, as without the check.
internal sealed class BindingTrace : ConfigurationProvider, IConfigurationSource
{
    private readonly AsyncLocal<List<string>?> recording = new();
    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;
    internal bool Enumerates(string section, Action action)
    {
        var paths = new List<string>();
        recording.Value = paths;
        try { action(); }
        catch (Exception) { }
        finally { recording.Value = null; }
        return paths.Any(p => p.Equals(section, StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(section + ConfigurationPath.KeyDelimiter, StringComparison.OrdinalIgnoreCase));
    }
    public override bool TryGet(string key, out string? value) { value = null; return false; }
    public override void Set(string key, string? value) { }
    public override IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
    {
        if (parentPath is not null) recording.Value?.Add(parentPath);
        return earlierKeys;
    }
}
