using ContractEngine.Api.Middleware;
using ContractEngine.Infrastructure.Configuration;
using Serilog;

Log.Logger = new Serilog.LoggerConfiguration()
    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter())
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter())
    .Enrich.FromLogContext());

builder.Services.AddContractEngineInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

public partial class Program { }
