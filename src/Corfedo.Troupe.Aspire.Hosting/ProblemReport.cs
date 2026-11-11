using System.Text;
using Aspire.Hosting.ApplicationModel;

namespace Corfedo.Troupe.Aspire.Hosting;

// One message for every problem of every Troupe resource, phrased in AppHost terms: sections as environment variable names,
// the resources that run a component whose absence makes a section needed, and endpoints that no listener serves.
internal static class ProblemReport
{
    private const string Indent = "  ";
    private const string RunTimeValue = "<run-time value>";

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
            foreach (var problem in Endpoints(resource.Resource, description)) lines.Add(Line(name, problem));
            // The description states TRP005 as configuration[].present and TRP103 as listeners[].configured and
            // unrequestedListeners; the package binds every listener, so an unconfigured one is not a problem.
            foreach (var diagnostic in description.Diagnostics.Where(d => d.Severity == "Error" && d.Code is not ("TRP005" or "TRP103")))
                lines.Add(Line(name, $"{diagnostic.Code}: {RunTime(diagnostic.Message)}"));
        }
        return lines.Count == 0 ? null : "Troupe resources cannot start:" + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    // Kestrel serves only the composition's listeners: a configured endpoint that no component requests fails the process
    // (TRP103), and once a listener is configured Kestrel ignores the addresses of every other HTTP endpoint.
    private static IEnumerable<string> Endpoints(ProjectResource resource, Description description)
    {
        var listeners = description.Listeners.Select(l => l.Name).ToArray();
        var unrequested = description.UnrequestedListeners.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var serving = listeners.Length == 0 ? "the composition requests no listeners" : $"listeners: {string.Join(", ", listeners)}";
        foreach (var endpoint in resource.Annotations.OfType<EndpointAnnotation>().Where(e => e.UriScheme is "http" or "https"))
        {
            if (listeners.Contains(endpoint.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (listeners.Length == 0 && !unrequested.Contains(endpoint.Name)) continue;
            unrequested.Remove(endpoint.Name);
            yield return $"endpoint \"{endpoint.Name}\" is not a listener of this composition ({serving}), so nothing serves it. Aspire adds " +
                "endpoints from the project's launch profile and Kestrel configuration: add the resource with AddTroupeProject, or remove the endpoint.";
        }
        foreach (var name in description.UnrequestedListeners.Where(unrequested.Contains))
            yield return $"TRP103: Kestrel endpoint {name} is configured, but no component of this composition requests it.";
    }

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
            text.AppendLine().Append($"{names.Get(component)} runs in " + string.Join(", ", hosts.Select(h =>
            {
                var listeners = h.Description!.Listeners.Select(l => l.Name).ToArray();
                return $"\"{h.Resource.Name}\"" + (listeners.Length == 0 ? "" : $" (listeners: {string.Join(", ", listeners)})");
            })) + ".");
        }
        return text.ToString();
    }

    // Arguments and environment values known only at run time are described as the placeholder; a message that quotes one
    // names it as such.
    private static string RunTime(string message) => !message.Contains(ProjectDescriber.Placeholder, StringComparison.Ordinal) ? message
        : message.Replace(ProjectDescriber.Placeholder, RunTimeValue, StringComparison.Ordinal) +
          $" {RunTimeValue} is an endpoint, parameter or expression that is not known when the resource is described; use a literal value.";

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
