using System.Text;
using Aspire.Hosting.ApplicationModel;

namespace Corfedo.Troupe.Aspire.Hosting;

// One message for every problem of every Troupe resource, phrased in AppHost terms: sections as environment variable names,
// and the resources that run a component whose absence makes a section needed.
internal static class ProblemReport
{
    private const string Indent = "  ";

    internal static string? Create(IReadOnlyList<DescribedResource> resources)
    {
        var names = new ShortNames(resources.SelectMany(r => r.Description is { } d
            ? d.Components.Concat(d.Configuration.SelectMany(s => s.Owners.Concat(s.UnlessComposed))) : []));
        var lines = new List<string>();
        foreach (var resource in resources)
        {
            var name = resource.Resource.Name;
            if (resource.Failure is { } failure) { lines.Add(Line(name, failure)); continue; }
            var description = resource.Description!;
            foreach (var section in description.Configuration.Where(s => !s.Present))
                lines.Add(Line(name, Needs(resource, section, resources, names)));
            var listeners = description.Listeners.Select(l => l.Name).ToArray();
            foreach (var diagnostic in description.Diagnostics.Where(d => d.Severity == "Error" && d.Code != "TRP005" && !Bound(d, listeners)))
                lines.Add(Line(name, $"{diagnostic.Code}: {diagnostic.Message}"));
        }
        return lines.Count == 0 ? null : "Troupe resources cannot start:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    // The package binds every listener, so a listener missing from Kestrel:Endpoints is not a problem.
    private static bool Bound(Diagnostic diagnostic, string[] listeners) => diagnostic.Code == "TRP103" &&
        listeners.Any(l => diagnostic.Message == $"Requested Kestrel endpoint {l} is missing from Kestrel:Endpoints.");

    private static string Needs(DescribedResource resource, Section section, IReadOnlyList<DescribedResource> resources, ShortNames names)
    {
        var variable = section.Name.Replace(":", "__", StringComparison.Ordinal);
        var readers = string.Join(", ", section.Owners.Select(names.Get));
        if (section.UnlessComposed.Count == 0) return $"needs {variable} (read by {readers}).";
        var absent = section.UnlessComposed.Select(names.Get).ToArray();
        var text = new StringBuilder($"needs {variable} (read by {readers} because {Join(absent)} {(absent.Length == 1 ? "is" : "are")} not in this process).");
        var project = resource.Resource.GetProjectMetadata().ProjectPath;
        foreach (var component in section.UnlessComposed)
        {
            var hosts = resources.Where(r => r != resource && r.Description is { } d && d.Components.Contains(component, StringComparer.Ordinal) &&
                r.Resource.GetProjectMetadata().ProjectPath == project).ToArray();
            if (hosts.Length == 0) { text.Append($" No Troupe resource runs {names.Get(component)}."); continue; }
            var host = hosts[0];
            var listeners = host.Description!.Listeners.Select(l => l.Name).ToArray();
            text.AppendLine().Append($"{names.Get(component)} runs in {(hosts.Length == 1 ? "resource" : "resources")} " +
                string.Join(", ", hosts.Select(h => $"\"{h.Resource.Name}\"")));
            if (listeners.Length == 0) { text.Append('.'); continue; }
            text.Append($" (listeners: {string.Join(", ", listeners)}), for example:").AppendLine()
                .Append($".WithEnvironment(\"{variable}\", {host.Resource.Name}.GetEndpoint(\"{listeners[0]}\"))");
        }
        return text.ToString();
    }

    private static string Join(string[] values) => values.Length == 1 ? values[0] : $"{string.Join(", ", values[..^1])} and {values[^1]}";

    // The first line follows the resource name; continuation lines align under the text after it.
    private static string Line(string resource, string text)
    {
        var prefix = $"{Indent}{resource}: ";
        var continuation = Environment.NewLine + new string(' ', prefix.Length);
        return prefix + string.Join(continuation, text.Split('\n').Select(l => l.TrimEnd('\r')));
    }

    // Type names shortened to the class name when no other name in the report has the same class name.
    private sealed class ShortNames
    {
        private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
        internal ShortNames(IEnumerable<string> names)
        {
            foreach (var group in names.Distinct(StringComparer.Ordinal).GroupBy(Short, StringComparer.Ordinal)) counts[group.Key] = group.Count();
        }
        internal string Get(string name) => counts.GetValueOrDefault(Short(name)) > 1 ? name : Short(name);
        private static string Short(string name) => name[(name.LastIndexOfAny(['.', '+']) + 1)..];
    }
}
