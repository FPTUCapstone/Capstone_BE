using System.Text.Json;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

using Serilog;

using TripMate.Api.Authorization;
using TripMate.Api.Common;
using TripMate.Api.Middleware;
using TripMate.Api.OpenApi;
using TripMate.Application;
using TripMate.Infrastructure;
using TripMate.Infrastructure.Authentication;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .WriteTo.Console());

    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    // AGENTS.md §5.4: credentials must come from environment variables or User Secrets,
    // never from committed configuration files.
    if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Default")))
    {
        throw new InvalidOperationException(
            "ConnectionStrings:Default is not configured. Set it via environment variable (ConnectionStrings__Default) or User Secrets.");
    }

    var jsonNamingPolicy = JsonNamingPolicy.CamelCase;
    builder.Services
        .AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = jsonNamingPolicy;

            // UC-04 v2.0 contract: enums serialize as strings (e.g. "Traveler", "Active") —
            // aligning the runtime serializer with the enum-as-string OpenAPI schema the API
            // already documents, so clients never depend on numeric enum values.
            options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        });

    builder.Services.Configure<ApiBehaviorOptions>(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var services = context.HttpContext.RequestServices;
            var problemDetailsFactory = services.GetRequiredService<ProblemDetailsFactory>();
            var serializerOptions = services
                .GetRequiredService<IOptions<JsonOptions>>()
                .Value
                .JsonSerializerOptions;
            var problem = problemDetailsFactory.CreateValidationProblemDetails(
                context.HttpContext,
                context.ModelState);

            ValidationErrorKeyNormalizer.NormalizeInPlace(
                problem.Errors,
                serializerOptions.PropertyNamingPolicy);

            if (context.HttpContext.Request.Path.StartsWithSegments("/api/v1/auth/web"))
                problem.Extensions["errorCode"] = TripMate.Application.Features.Authentication.Common.AuthErrorCodes.RequestInvalid;

            var result = new BadRequestObjectResult(problem);
            result.ContentTypes.Add("application/problem+json");
            return result;
        };
    });

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo { Title = "TripMate API", Version = "v1" });
        options.SchemaFilter<PoiEnumSchemaFilter>();
        options.SchemaFilter<PoiContractSchemaFilter>();
        options.SchemaFilter<ProblemDetailsContractSchemaFilter>();
        options.OperationFilter<AllowAnonymousOperationFilter>();
        options.OperationFilter<TourSearchOperationFilter>();

        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "Bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Enter a valid JWT access token.",
        });

        options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
        {
            { new OpenApiSecuritySchemeReference("Bearer", document), new List<string>() },
        });
    });

    var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
    var jwtOptions = jwtSection.Get<JwtOptions>() ?? new JwtOptions();
    var signingKey = !string.IsNullOrWhiteSpace(jwtOptions.SigningKey)
        ? jwtOptions.SigningKey
        : jwtSection["SigningKey"];
    if (string.IsNullOrWhiteSpace(signingKey))
    {
        throw new InvalidOperationException(
            "Jwt:SigningKey is not configured. Set it via environment variable or User Secrets.");
    }
    var issuer = !string.IsNullOrWhiteSpace(jwtOptions.Issuer) ? jwtOptions.Issuer : "TripMate";
    var audience = !string.IsNullOrWhiteSpace(jwtOptions.Audience) ? jwtOptions.Audience : "TripMateClients";

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = issuer,
                ValidAudience = audience,
                IssuerSigningKey = new SymmetricSecurityKey(
                    Convert.FromBase64String(signingKey)),
                ClockSkew = TimeSpan.FromMinutes(1),
            };
        });

    builder.Services.AddAuthorization();
    builder.Services.AddSingleton<
        IAuthorizationMiddlewareResultHandler,
        ProblemDetailsAuthorizationMiddlewareResultHandler>();

    const string corsPolicyName = "TripMateClients";
    builder.Services.AddCors(options =>
    {
        options.AddPolicy(corsPolicyName, policy =>
        {
            var allowedOrigins = builder.Configuration
                .GetSection("Cors:AllowedOrigins")
                .Get<string[]>() ?? [];

            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        });
    });

    // UC-06 password reset: account-independent per-IP abuse limiting. The account-level
    // 60-second resend cooldown stays inside the request flow (generic 200) and never
    // becomes an HTTP 429.
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = static async (context, cancellationToken) =>
        {
            // The 429 payload matches the documented ProblemDetails contract and carries no
            // account information — only the fact that this transport/IP is throttled.
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.HttpContext.Response.WriteAsJsonAsync(
                new ProblemDetails
                {
                    Title = "Too many requests.",
                    Status = StatusCodes.Status429TooManyRequests,
                },
                cancellationToken);
        };
        options.AddPolicy(PasswordResetRateLimiter.PolicyName, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PasswordResetRateLimiter.PermitLimit,
                    Window = PasswordResetRateLimiter.Window,
                    QueueLimit = 0,
                }));
    });

    var app = builder.Build();

    // Outer middleware observes the status after validation exceptions are mapped to 400.
    app.UseMiddleware<RequestRejectionLoggingMiddleware>();
    app.UseMiddleware<ExceptionHandlingMiddleware>();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseCors(corsPolicyName);

    app.UseHttpsRedirection();

    app.UseAuthentication();
    app.UseAuthorization();

    app.UseRateLimiter();

    app.MapControllers();

    app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    // HostAbortedException is thrown intentionally by `dotnet ef` design-time tooling
    // (e.g. `dotnet ef migrations add`) while probing the app's DI container; it is not a
    // real startup failure and should not be logged as one.
    Log.Fatal(ex, "TripMate API terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}