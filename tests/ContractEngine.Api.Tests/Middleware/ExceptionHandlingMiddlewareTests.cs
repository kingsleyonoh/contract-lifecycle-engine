using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ContractEngine.Api.Tests.Middleware;

[Collection(WebApplicationCollection.Name)]
public class ExceptionHandlingMiddlewareTests : IClassFixture<ExceptionHandlingTestFactory>
{
    private readonly ExceptionHandlingTestFactory _factory;

    public ExceptionHandlingMiddlewareTests(ExceptionHandlingTestFactory factory)
    {
        _factory = factory;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    [Fact]
    public async Task ValidationException_Returns400_WithValidationErrorCode()
    {
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/__tests__/throw/validation");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var json = await ReadJsonAsync(resp);
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("VALIDATION_ERROR");
        error.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task KeyNotFound_Returns404_WithNotFoundCode()
    {
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/__tests__/throw/notfound");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var json = await ReadJsonAsync(resp);
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task GenericException_Returns500_WithInternalErrorCode_AndDoesNotLeakStackTrace()
    {
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/__tests__/throw/generic");

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await resp.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("INTERNAL_ERROR");
        body.Should().NotContain("at ContractEngine.", "stack traces must not leak to clients");
        body.Should().NotContain("System.Exception:", "exception type name must not leak");
    }

    [Fact]
    public async Task EveryErrorResponse_HasNonEmptyRequestId()
    {
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/__tests__/throw/generic");
        using var json = await ReadJsonAsync(resp);
        var requestId = json.RootElement.GetProperty("error").GetProperty("request_id").GetString();
        requestId.Should().NotBeNullOrWhiteSpace();
    }
}

public class ExceptionHandlingTestFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        // Force Production environment so the middleware suppresses exception detail.
        Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions
            .UseEnvironment(builder, Microsoft.Extensions.Hosting.Environments.Production);

        Microsoft.AspNetCore.Hosting.WebHostBuilderExtensions.Configure(builder, app =>
        {
            app.UseMiddleware<ContractEngine.Api.Middleware.ExceptionHandlingMiddleware>();
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/__tests__/throw/validation", () =>
                {
                    var failures = new List<FluentValidation.Results.ValidationFailure>
                    {
                        new("end_date", "Must be after effective_date"),
                    };
                    throw new FluentValidation.ValidationException(failures);
                });
                endpoints.MapGet("/__tests__/throw/notfound", () =>
                {
                    throw new KeyNotFoundException("widget does not exist");
                });
                endpoints.MapGet("/__tests__/throw/generic", () =>
                {
                    throw new Exception("boom internal secret should not leak");
                });
            });
        });
    }
}
