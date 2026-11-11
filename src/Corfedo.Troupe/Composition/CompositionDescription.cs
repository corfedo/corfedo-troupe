using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Corfedo.Troupe;

// Describe mode: when TROUPE_DESCRIBE names a file, AddTroupe composes the process's own selection, writes format 1 to that
// file and ends the process with exit code 0, so code after AddTroupe never runs. A composition error is part of the
// description, not a failure of it.
internal static class CompositionDescription
{
    internal const string Variable = "TROUPE_DESCRIBE";
    internal const int Format = 1;

    // Tests describe in process: the request carries the output path and replaces the process exit within their async flow.
    internal static readonly AsyncLocal<Request?> Override = new();
    internal sealed record Request(string Output, Action Exit);

    internal static Request? Requested() => Override.Value ??
        (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } output ? new(output, () => Environment.Exit(0)) : null);

    internal static void Describe<T>(IHostApplicationBuilder builder, Request request) where T : ICompositionDefinition, new()
    {
        DefinitionBuilder? definition = null;
        CompositionManifest? manifest = null;
        IReadOnlyList<CompositionDiagnostic> diagnostics;
        try
        {
            definition = DefinitionBuilder.Create<T>();
            var declared = definition;
            manifest = CompositionExtensions.Compose(builder, () => CompositionExtensions.Resolve(declared, builder.Configuration));
            diagnostics = manifest.Diagnostics;
        }
        catch (CompositionException exception)
        {
            manifest = exception.Manifest;
            diagnostics = manifest?.Diagnostics ?? exception.Diagnostics;
        }
        Write(request.Output, writer => WriteDescription(writer, typeof(T), definition, CompositionSession.TryGet(builder), manifest,
            diagnostics, builder.Configuration));
        // Without this line a process that inherits the variable by mistake would stop with exit code 0 and no output.
        Console.Error.WriteLine($"{Variable} is set: Troupe wrote this process's composition to {Path.GetFullPath(request.Output)} and ends the process without serving.");
        request.Exit();
    }

    private static void WriteDescription(Utf8JsonWriter writer, Type definitionType, DefinitionBuilder? definition, CompositionSession? session,
        CompositionManifest? manifest, IReadOnlyList<CompositionDiagnostic> diagnostics, IConfiguration configuration)
    {
        writer.WriteStartObject();
        writer.WriteNumber("format", Format);
        writer.WriteString("definition", definitionType.FullName);
        writer.WriteStartArray("profiles");
        foreach (var profile in (definition?.Profiles ?? []).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", profile.Name);
            Strings(writer, "roles", profile.Roles);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        Strings(writer, "roles", definition?.Roles.Keys ?? Enumerable.Empty<string>());
        // A session exists once the selection resolved; its manifest lists the composed contributors even after a failure.
        if (session is null || manifest is null) writer.WriteNull("selection");
        else
        {
            writer.WriteStartObject("selection");
            if (session.Runtime.Profile is { } selected) writer.WriteString("profile", selected.Name);
            else writer.WriteNull("profile");
            Strings(writer, "roles", session.Runtime.Roles.Select(r => r.Name));
            writer.WriteEndObject();
        }
        Strings(writer, "components", (manifest?.Contributors ?? []).Where(c => c.Owner.Kind == OwnerKind.Component).Select(c => c.Owner.Name));
        var listeners = session?.Listeners() ?? ListenerState.None;
        writer.WriteStartArray("listeners");
        foreach (var listener in listeners.Requested.OrderBy(l => l.Name, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", listener.Name);
            writer.WriteBoolean("configured", listener.Configured);
            Strings(writer, "owners", listener.Owners.Select(o => o.Name));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        Strings(writer, "unrequestedListeners", listeners.Unrequested);
        writer.WriteStartArray("configuration");
        if (session is not null && manifest is not null)
            foreach (var section in manifest.Configuration.Where(r => CoreRules.Needed(r, session.Runtime))
                         .GroupBy(r => r.Section, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartObject();
                writer.WriteString("section", section.Key);
                writer.WriteBoolean("present", configuration.GetSection(section.Key).Exists());
                Strings(writer, "owners", section.Select(r => r.Owner.Name));
                // A required read makes the section needed whatever is composed; otherwise each absent component is a reason.
                if (!section.Any(r => r.Required))
                    Strings(writer, "unlessComposed", section.Select(r => r.UnlessComposed!.FullName ?? r.UnlessComposed.Name));
                writer.WriteEndObject();
            }
        writer.WriteEndArray();
        writer.WriteStartArray("diagnostics");
        foreach (var diagnostic in diagnostics.OrderBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("code", diagnostic.Code);
            writer.WriteString("severity", diagnostic.Severity.ToString());
            writer.WriteString("message", diagnostic.Message);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    // Readers never see a partial file: the description is written next to the target and moved over it.
    private static void Write(string output, Action<Utf8JsonWriter> write)
    {
        var target = Path.GetFullPath(output);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = File.Create(temporary))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                write(writer);
            File.Move(temporary, target, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
