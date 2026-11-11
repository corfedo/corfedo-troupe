using System.Diagnostics;
using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Corfedo.Troupe.Aspire.Hosting;

// Runs a project resource in Troupe's describe mode with the resource's own arguments and environment.
internal static class ProjectDescriber
{
    internal const string Placeholder = "http://troupe-describe.invalid";
    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
    private static readonly string DescribeVersion = typeof(ProjectDescriber).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "TroupeDescribeVersion").Value!;

    internal static async Task<DescribeResult> DescribeAsync(ProjectResource resource, DistributedApplicationExecutionContext execution,
        string? configuration, CancellationToken cancellationToken)
    {
        var project = resource.GetProjectMetadata().ProjectPath;
        var arguments = new List<object>();
        var environment = new Dictionary<string, object>();
        // Aspire runs these callbacks again when it starts the resource, after endpoints are allocated. Here nothing is
        // allocated yet, so a callback that reads an allocated value (for example EndpointReference.Url) fails.
        try
        {
            foreach (var callback in resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
                await callback.Callback(new CommandLineArgsCallbackContext(arguments, resource, cancellationToken)).ConfigureAwait(false);
            var context = new EnvironmentCallbackContext(execution, resource, environment, cancellationToken);
            foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
                await callback.Callback(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DescribeResult.Failed("an argument or environment callback failed before resources start: " +
                $"{exception.Message} The callbacks of a Troupe resource also run once before any resource starts, when nothing is " +
                "allocated; pass the endpoint reference or a ReferenceExpression as the value instead of reading it in the callback.");
        }

        var output = Path.Combine(Path.GetTempPath(), $"troupe-describe-{Guid.NewGuid():N}.json");
        // The project runs from its directory, as Aspire runs it, so relative content roots and configuration files match.
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(project)!, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "run", "--project", project, "--no-build", "--no-launch-profile" }) start.ArgumentList.Add(argument);
        if (!string.IsNullOrEmpty(configuration)) { start.ArgumentList.Add("--configuration"); start.ArgumentList.Add(configuration); }
        start.ArgumentList.Add("--");
        foreach (var argument in arguments) start.ArgumentList.Add(Value(argument));
        foreach (var (name, value) in environment) start.Environment[name] = Value(value);
        start.Environment["TROUPE_DESCRIBE"] = output;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet could not be started.");
        var standardOutput = Drain(process.StandardOutput);
        var standardError = Drain(process.StandardError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(output))
                return DescribeResult.Failed($"the project could not describe itself (exit code {process.ExitCode}).{Tail(await standardError.ConfigureAwait(false), await standardOutput.ConfigureAwait(false))}");
            return DescribeResult.Described(Description.Parse(await File.ReadAllTextAsync(output, cancellationToken).ConfigureAwait(false)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DescribeResult.Failed($"the project did not describe itself within {Timeout.TotalSeconds:0} seconds: it must call AddTroupe from " +
                $"Corfedo.Troupe {DescribeVersion} or later, and reach it without waiting on external services.");
        }
        catch (FormatException exception) { return DescribeResult.Failed(exception.Message); }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            File.Delete(output);
        }
    }

    // Values known only at run time (endpoints, expressions, parameters, connection strings) become the placeholder.
    private static string Value(object? value) => value as string ?? Placeholder;

    private static async Task<string> Drain(StreamReader reader)
    {
        var lines = new Queue<string>();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            lines.Enqueue(line);
            if (lines.Count > 20) lines.Dequeue();
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string Tail(string error, string output)
    {
        var text = string.IsNullOrWhiteSpace(error) ? output : error;
        return string.IsNullOrWhiteSpace(text) ? "" : Environment.NewLine + text.Trim();
    }
}

internal sealed record DescribeResult(Description? Description, string? Failure)
{
    internal static DescribeResult Described(Description description) => new(description, null);
    internal static DescribeResult Failed(string failure) => new(null, failure);
}
