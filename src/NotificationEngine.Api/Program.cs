using System.Diagnostics;
using System.Security.Claims;
using Hangfire;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using NotificationEngine.Api;
using NotificationEngine.Api.Hubs;
using NotificationEngine.Application;
using NotificationEngine.Application.Abstractions.Jobs;
using NotificationEngine.Infrastructure;
using NotificationEngine.Infrastructure.Persistence;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "NotificationEngine.Api")
    .Enrich.WithProperty("MachineName", Environment.MachineName)
    .WriteTo.Console()
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Authentication:Authority"];
        options.Audience = builder.Configuration["Authentication:Audience"];
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            NameClaimType = ClaimTypes.NameIdentifier,
            RoleClaimType = ClaimTypes.Role
        };
        
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                if (!string.IsNullOrEmpty(accessToken) && 
                    path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILogger<Program>>();

                if (context.Principal?.FindFirst(ClaimTypes.NameIdentifier) is { } userId)
                {
                    logger.LogInformation(
                        "JWT validated for user: {UserId}",
                        userId);
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("UserId", policy =>
        policy.RequireClaim(ClaimTypes.NameIdentifier));
    
    options.AddPolicy("TenantId", policy =>
        policy.RequireClaim("tenant_id"));
    
    options.AddPolicy("UserIdOrSystem", policy =>
        policy.RequireAssertion(context =>
            context.User.HasClaim(c => c.Type == ClaimTypes.NameIdentifier) ||
            context.User.HasClaim(c => c.Type == "system")));
});

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource("NotificationEngine.*"));

builder.Services.AddHttpContextAccessor();

var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
    ?? builder.Configuration["Redis:ConnectionString"]
    ?? "localhost:6379";

builder.Services.AddSignalR()
    .AddStackExchangeRedis(redisConnectionString, options =>
    {
        options.Configuration.ChannelPrefix = RedisChannel.Literal("signalr");
        options.Configuration.ConnectRetry = 5;
        options.Configuration.ReconnectRetryPolicy = new LinearRetry(500);
    });

builder.Services.AddSingleton<IUserIdProvider, UserIdProvider>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    dbContext.Database.EnsureCreated();
}

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireAuthFilter() }
});

using (var scope = app.Services.CreateScope())
{
    var recurringJobManager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    var outboxJob = scope.ServiceProvider.GetRequiredService<IOutboxPublisherJob>();
    
    recurringJobManager.AddOrUpdate(
        "outbox-publisher",
        () => outboxJob.ExecuteAsync(default),
        "*/5 * * * *",
        new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc,
            MisfireHandling = MisfireHandlingMode.Relaxed
        });
}

app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        var traceId = Activity.Current?.TraceId.ToString() ?? 
                      httpContext.TraceIdentifier;
        var spanId = Activity.Current?.SpanId.ToString();
        
        diagnosticContext.Set("TraceId", traceId);
        diagnosticContext.Set("SpanId", spanId);
        
        if (httpContext.User.FindFirst(ClaimTypes.NameIdentifier) is { } userId)
        {
            diagnosticContext.Set("UserId", userId);
        }
        
        if (httpContext.User.FindFirst("tenant_id") is { } tenantId)
        {
            diagnosticContext.Set("TenantId", tenantId);
        }
    };
});

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "NotificationEngine API v1");
    c.RoutePrefix = "";
});

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
    .WithTags("Health");

app.MapGet("/health/ready", () => Results.Ok(new { status = "ready", timestamp = DateTime.UtcNow }))
    .WithTags("Health");

app.MapGet("/health/live", () => Results.Ok(new { status = "live", timestamp = DateTime.UtcNow }))
    .WithTags("Health");

app.MapHub<DashboardHub>("/hubs/dashboard")
    .RequireAuthorization();

try
{
    Log.Information("Starting NotificationEngine API");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
