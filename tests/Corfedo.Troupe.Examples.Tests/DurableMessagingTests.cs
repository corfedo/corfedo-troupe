using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Corfedo.Troupe.AspNetCore;
using Corfedo.Troupe.Examples.Messaging;
using Corfedo.Troupe.Examples.Tests.Fixtures;
using Corfedo.Troupe.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace Corfedo.Troupe.Examples.Tests;

public sealed class DurableMessagingTests
{
    private const string PostgresImage = "postgres:18.6@sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722";

    [Fact]
    public async Task DurableProfilesVerifyWithAnExplicitInertMessageStore()
    {
        var result = await CompositionVerifier.VerifyProfilesAsync<DurableMessagingComposition>(
            profile => MessagingApplication.CreateDurableBuilder(
                ["--profile", profile.Name, "--Messaging:ConnectionString", "Host=127.0.0.1;Port=1;Database=unused;Username=unused", "--Kestrel:Endpoints:public:Url", "http://127.0.0.1:0"],
                ApplicationExamplesTests.ContentRoot("Messaging")),
            (_, services) => services.Replace(ServiceDescriptor.Singleton<IMessageStore, NullMessageStore>()),
            TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.ToString());
        Assert.Equal(4, result.Profiles.Count);
    }

    [Fact]
    public async Task StaticConsumersReceiveAcrossProcessesAndRecoverQueuesAfterRestart()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        await using var database = new PostgreSqlBuilder(PostgresImage).WithDatabase("troupe").Build();
        await database.StartAsync(token);
        var connectionString = database.GetConnectionString();
        await using (var missing = new MessagingProcess(connectionString, ["durable", "--profile", "orders"]))
        {
            var failure = await missing.ExpectFailureAsync(token);
            Assert.Contains("troupe_messages", failure);
        }
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM information_schema.tables WHERE table_schema IN ('troupe_messages', 'troupe_queues')";
            Assert.Equal(0L, await command.ExecuteScalarAsync(token));
        }
        await using (var setup = new MessagingProcess(connectionString, ["durable", "resources", "setup"]))
            await setup.ExpectExitAsync(token);

        var incompleteBuilder = MessagingApplication.CreateDurableBuilder(
            ["--profile", "combined", "--Messaging:ConnectionString", connectionString,
             "--Kestrel:Endpoints:public:Url", "http://127.0.0.1:0"], ApplicationExamplesTests.ContentRoot("Messaging"));
        await using (var incomplete = incompleteBuilder.Build())
        {
            incomplete.UseComposition();
            incomplete.Services.GetRequiredService<WolverineOptions>().ApplicationAssembly = typeof(ArtifactAssembly).Assembly;
            var failure = await Assert.ThrowsAsync<MissingPreBuiltTypesException>(() => incomplete.StartAsync(token));
            Assert.Contains(nameof(SendNotificationHandler), failure.Message);
        }

        await using var publisher = new MessagingProcess(connectionString, ["durable", "--profile", "publisher"]);
        using var publishing = await publisher.ClientAsync(token);
        await using (var orders = new MessagingProcess(connectionString, ["durable", "--profile", "orders"]))
        {
            using var client = await orders.ClientAsync(token);
            var receipt = await SubmitAndReceive(publishing, client, "orders", token);
            Assert.Equal("orders", receipt.Consumer);
            using var absent = await client.PostAsJsonAsync("/notifications/queued", new SendNotification("excluded", "reader@example.test"), token);
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
            orders.Kill();
        }
        await using (var notifications = new MessagingProcess(connectionString, ["durable", "--profile", "notifications"]))
        {
            using var client = await notifications.ClientAsync(token);
            Assert.Equal("notifications", (await SubmitAndReceive(publishing, client, "notifications", token)).Consumer);
        }

        var orderId = Guid.NewGuid().ToString("N");
        var notificationId = Guid.NewGuid().ToString("N");
        await Submit(publishing, "orders", orderId, token);
        await Submit(publishing, "notifications", notificationId, token);
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM troupe_queues.wolverine_queue_orders";
            Assert.True(Convert.ToInt64(await command.ExecuteScalarAsync(token)) > 0);
            command.CommandText = "SELECT count(*) FROM troupe_queues.wolverine_queue_notifications";
            Assert.True(Convert.ToInt64(await command.ExecuteScalarAsync(token)) > 0);
        }
        // The acknowledged messages must survive loss of both publishing and consuming processes.
        publisher.Kill();
        await using var restarted = new MessagingProcess(connectionString, ["durable", "--profile", "combined"]);
        using var restartedClient = await restarted.ClientAsync(token);
        var order = await Receipt(restartedClient, orderId, token);
        var notification = await Receipt(restartedClient, notificationId, token);
        Assert.Equal("orders", order.Consumer);
        Assert.Equal("notifications", notification.Consumer);
        Assert.NotEqual(order.ScopeId, notification.ScopeId);
    }

    private static async Task<DeliveryReceipt> SubmitAndReceive(HttpClient publisher, HttpClient consumer, string queue, CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        await Submit(publisher, queue, id, token);
        return await Receipt(consumer, id, token);
    }

    private static async Task Submit(HttpClient client, string queue, string id, CancellationToken token)
    {
        using var response = queue == "orders"
            ? await client.PostAsJsonAsync("/orders/queued", new PlaceOrder(id), token)
            : await client.PostAsJsonAsync("/notifications/queued", new SendNotification(id, "reader@example.test"), token);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private static async Task<DeliveryReceipt> Receipt(HttpClient client, string id, CancellationToken token)
    {
        while (true)
        {
            using var response = await client.GetAsync($"/receipts/{id}", token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var receipt = await response.Content.ReadFromJsonAsync<DeliveryReceipt>(token);
                Assert.Equal(id, receipt!.Id);
                return receipt;
            }
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await Task.Delay(100, token);
        }
    }

    private sealed class MessagingProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task output;
        private readonly Task error;
        private readonly ConcurrentQueue<string> lines = new();
        private readonly TaskCompletionSource<Uri> address = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MessagingProcess(string connectionString, string[] args)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = ApplicationExamplesTests.ContentRoot("Messaging")
            };
            var executable = typeof(DurableMessagingTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "MessagingExecutable").Value!;
            start.ArgumentList.Add(Path.GetFullPath(executable));
            foreach (var argument in args) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("--Kestrel:Endpoints:public:Url");
            start.ArgumentList.Add("http://127.0.0.1:0");
            start.Environment["Messaging__ConnectionString"] = connectionString;
            process = Process.Start(start)!;
            output = Read(process.StandardOutput);
            error = Read(process.StandardError);
        }

        private async Task Read(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lines.Enqueue(line);
                const string prefix = "Now listening on: ";
                var index = line.IndexOf(prefix, StringComparison.Ordinal);
                if (index >= 0) address.TrySetResult(new Uri(line[(index + prefix.Length)..].Trim()));
            }
        }

        public async Task<HttpClient> ClientAsync(CancellationToken token)
        {
            var exited = process.WaitForExitAsync(token);
            var ready = address.Task.WaitAsync(token);
            if (await Task.WhenAny(ready, exited) == exited)
            {
                await exited;
                await Task.WhenAll(output, error);
                Assert.Fail(string.Join(Environment.NewLine, lines));
            }
            return new HttpClient { BaseAddress = await ready };
        }

        public async Task<string> ExpectFailureAsync(CancellationToken token)
        {
            await process.WaitForExitAsync(token);
            await Task.WhenAll(output, error);
            var log = string.Join(Environment.NewLine, lines);
            Assert.True(process.ExitCode != 0, log);
            return log;
        }

        public async Task ExpectExitAsync(CancellationToken token)
        {
            await process.WaitForExitAsync(token);
            await Task.WhenAll(output, error);
            Assert.True(process.ExitCode == 0, string.Join(Environment.NewLine, lines));
        }

        public void Kill() { if (!process.HasExited) process.Kill(entireProcessTree: true); }

        public async ValueTask DisposeAsync()
        {
            Kill();
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
            process.Dispose();
        }
    }
}
