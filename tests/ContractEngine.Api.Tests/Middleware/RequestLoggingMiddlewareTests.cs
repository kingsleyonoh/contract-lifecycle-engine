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

[Collection(WebApplicationCollection.Name)]
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

    /// <summary>
    /// PRD §9 + §10b: the <c>module</c> enricher must derive from the URL structure:
    /// <c>/api/&lt;module&gt;/...</c> → <c>&lt;module&gt;</c>; non-API paths → first segment;
    /// root / empty → <c>"http"</c>. Pins the behavior before Batch 025 observability work.
    /// </summary>
    [Theory]
    [InlineData("/api/contracts/abc-123", "contracts")]
    [InlineData("/api/webhooks/contract-signed", "webhooks")]
    [InlineData("/api/obligations", "obligations")]
    [InlineData("/health", "health")]
    [InlineData("/", "http")]
    public async Task Module_Property_IsDerivedFromPathShape(string path, string expectedModule)
    {
        InMemoryLogSink.Instance.Clear();
        using var client = _factory.CreateClient();

        // We send to /__tests__/logged but we'll invoke a route with the given shape below.
        // For the real test factory, only /__tests__/logged is mapped. Use the probe endpoint
        // that echoes the derived module so we verify the DeriveModule helper directly.
        var probe = await client.GetAsync($"/__tests__/module-probe?path={Uri.EscapeDataString(path)}");
        probe.StatusCode.Should().Be(HttpStatusCode.OK, because: "the probe endpoint is always reachable");
        var body = (await probe.Content.ReadAsStringAsync()).Trim('"');
        body.Should().Be(expectedModule);
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

                // Exposes RequestLoggingMiddleware.DeriveModule so the theory test can assert
                // the enricher's URL → module derivation without reaching into a private helper.
                endpoints.MapGet("/__tests__/module-probe",
                    (Microsoft.AspNetCore.Http.HttpRequest req) =>
                    {
                        var probed = req.Query["path"].ToString();
                        var module = RequestLoggingMiddleware.DeriveModule(
                            new Microsoft.AspNetCore.Http.PathString(probed));
                        return Microsoft.AspNetCore.Http.Results.Ok(module);
                    });
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
