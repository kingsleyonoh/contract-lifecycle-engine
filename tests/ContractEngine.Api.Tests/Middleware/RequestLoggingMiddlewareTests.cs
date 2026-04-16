using System.Net;
using ContractEngine.Api.Middleware;
using ContractEngine.Core.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Xunit;

namespace ContractEngine.Api.Tests.Middleware;

public class RequestLoggingMiddlewareTests : IClassFixture<RequestLoggingTestFactory>
{
    private readonly RequestLoggingTestFactory _factory;

    public RequestLoggingMiddlewareTests(RequestLoggingTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Request_EmitsLogEvent_WithRequestIdAndTenantIdAndModule()
    {
        InMemoryLogSink.Instance.Clear();
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/__tests__/logged");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var events = InMemoryLogSink.Instance.Snapshot();
        events.Should().NotBeEmpty("RequestLoggingMiddleware must emit at least one log event per request");

        var hasRequestId = events.Any(e => e.Properties.ContainsKey("request_id"));
        hasRequestId.Should().BeTrue("log events must be enriched with request_id");

        var hasModule = events.Any(e => e.Properties.ContainsKey("module"));
        hasModule.Should().BeTrue("log events must be enriched with module");

        var hasTenantIdProperty = events.Any(e => e.Properties.ContainsKey("tenant_id"));
        hasTenantIdProperty.Should().BeTrue("log events must include tenant_id (may be null when unresolved)");
    }

    [Fact]
    public async Task Request_EmitsCompletionLog_WithStatusCodeAndElapsedMs()
    {
        InMemoryLogSink.Instance.Clear();
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/__tests__/logged");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var events = InMemoryLogSink.Instance.Snapshot();

        var completion = events.FirstOrDefault(e =>
            e.Properties.ContainsKey("StatusCode") && e.Properties.ContainsKey("ElapsedMs"));
        completion.Should().NotBeNull("a completion log event with StatusCode and ElapsedMs must be emitted");

        completion!.Properties["StatusCode"].ToString().Should().Contain("200");
        var elapsed = completion.Properties["ElapsedMs"].ToString();
        elapsed.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Request_RequestIdPropertyMatchesHttpContextTraceIdentifier()
    {
        InMemoryLogSink.Instance.Clear();
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/__tests__/logged");
        var traceId = (await resp.Content.ReadAsStringAsync()).Trim('"');

        var events = InMemoryLogSink.Instance.Snapshot();
        var requestIdEvent = events.First(e => e.Properties.ContainsKey("request_id"));
        var loggedRequestId = requestIdEvent.Properties["request_id"].ToString().Trim('"');

        loggedRequestId.Should().Be(traceId, "the request_id in logs must equal HttpContext.TraceIdentifier");
    }
}

public class RequestLoggingTestFactory : WebApplicationFactory<Program>
{
    static RequestLoggingTestFactory()
    {
        // Static Log.Logger picks up the InMemoryLogSink + LogContext enricher so that
        // LogContext.PushProperty("request_id", ...) calls inside the middleware surface as
        // properties on emitted events.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(InMemoryLogSink.Instance)
            .CreateLogger();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Re-register Serilog AFTER Program.cs so our logger wins in the DI container.
        builder.UseSerilog(Log.Logger, dispose: false);
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions
            .UseEnvironment(builder, Environments.Development);

        Microsoft.AspNetCore.Hosting.WebHostBuilderExtensions.Configure(builder, app =>
        {
            app.UseMiddleware<RequestLoggingMiddleware>();
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/__tests__/logged",
                    (Microsoft.AspNetCore.Http.HttpContext ctx) =>
                        Microsoft.AspNetCore.Http.Results.Ok(ctx.TraceIdentifier));
            });
        });

        Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(
            builder,
            services =>
            {
                services.AddScoped<ITenantContext, NullTenantContext>();
            });
    }
}
