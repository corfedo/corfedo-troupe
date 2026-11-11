using System.Text.Json;

namespace Corfedo.Troupe.Aspire.Hosting;

// Format 1 of the description a Troupe application writes in describe mode.
internal sealed record Description(
    string Definition,
    IReadOnlyList<string> Roles,
    Selection? Selection,
    IReadOnlyList<string> Components,
    IReadOnlyList<Listener> Listeners,
    IReadOnlyList<string> UnrequestedListeners,
    IReadOnlyList<Section> Configuration,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    internal static Description Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("format", out var format) || format.GetInt32() != 1)
            throw new FormatException("The project wrote a description in a format other than 1; use the same Troupe version in the AppHost and the project.");
        var selection = root.GetProperty("selection");
        return new(
            root.GetProperty("definition").GetString()!,
            Strings(root.GetProperty("roles")),
            selection.ValueKind == JsonValueKind.Null ? null
                : new(selection.GetProperty("profile").GetString(), Strings(selection.GetProperty("roles"))),
            Strings(root.GetProperty("components")),
            [.. root.GetProperty("listeners").EnumerateArray().Select(l => new Listener(l.GetProperty("name").GetString()!,
                l.GetProperty("configured").GetBoolean(), Strings(l.GetProperty("owners"))))],
            Strings(root.GetProperty("unrequestedListeners")),
            [.. root.GetProperty("configuration").EnumerateArray().Select(c => new Section(c.GetProperty("section").GetString()!,
                c.GetProperty("present").GetBoolean(), Strings(c.GetProperty("owners")),
                c.TryGetProperty("unlessComposed", out var unless) ? Strings(unless) : []))],
            [.. root.GetProperty("diagnostics").EnumerateArray().Select(d => new Diagnostic(d.GetProperty("code").GetString()!,
                d.GetProperty("severity").GetString()!, d.GetProperty("message").GetString()!))]);
    }

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];
}

internal sealed record Selection(string? Profile, IReadOnlyList<string> Roles);
internal sealed record Listener(string Name, bool Configured, IReadOnlyList<string> Owners);
// UnlessComposed is empty for a section that a required read needs.
internal sealed record Section(string Name, bool Present, IReadOnlyList<string> Owners, IReadOnlyList<string> UnlessComposed);
internal sealed record Diagnostic(string Code, string Severity, string Message);
