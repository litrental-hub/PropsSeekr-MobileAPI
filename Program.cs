using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using PropSeekr.Data;
using PropSeekr.Services;
using PropSeekr.Services.Interfaces;
using PropSeekr.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Local development can supply ConnectionStrings:DefaultConnection through User Secrets or an
// environment variable. All other deployments obtain it from AWS Secrets Manager.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    var secretName = builder.Configuration["AWS:DatabaseSecretName"];
    if (string.IsNullOrWhiteSpace(secretName))
    {
        throw new InvalidOperationException(
            "Configure ConnectionStrings:DefaultConnection through User Secrets/environment for local development, " +
            "or configure AWS:DatabaseSecretName for AWS Secrets Manager.");
    }

    var region = builder.Configuration["AWS:Region"] ?? "ap-south-1";
    using var secretsClient = new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region));
    var secretResponse = secretsClient.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretName }).GetAwaiter().GetResult();
    var secretString = secretResponse.SecretString ?? throw new InvalidOperationException("The database secret does not contain a SecretString.");

    try
    {
        using var document = JsonDocument.Parse(secretString);
        connectionString = document.RootElement.TryGetProperty("ConnectionString", out var connectionStringProperty)
            ? connectionStringProperty.GetString()
            : null;
    }
    catch (JsonException)
    {
        connectionString = secretString;
    }
}

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("The database secret must contain a complete connection string.");
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        connectionString ?? throw new InvalidOperationException("DefaultConnection is not configured."),
        o => o.UseNetTopologySuite()));

// Services
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("PlayIntegrity", client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IOtpDeliveryService, Msg91OtpDeliveryService>();
builder.Services.AddScoped<IEmailService, AmazonSesEmailService>();
builder.Services.AddScoped<IEmailOtpService, EmailOtpService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IProfileService, ProfileService>();
builder.Services.AddScoped<ISearchPropertyService, SearchPropertyService>();
builder.Services.AddScoped<IPropertyInventoryService, PropertyInventoryService>();
builder.Services.AddScoped<IRazorpayService, RazorpayService>();
builder.Services.AddScoped<IUserMatchesService, UserMatchesService>();
builder.Services.AddScoped<ICurrentUserContext, CurrentUserContext>();
builder.Services.AddScoped<IAppAttestationService, AppAttestationService>();
builder.Services.AddScoped<IAuthorizationHandler, AppAttestationAuthorizationHandler>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("CustomerPolicy", policy =>
    {
        policy.AddAuthenticationSchemes("Cognito");
        policy.RequireAuthenticatedUser();
    });

    options.AddPolicy("AdminPolicy", policy =>
    {
        policy.AddAuthenticationSchemes("AdminJwt");
        policy.RequireAuthenticatedUser();
        policy.RequireRole("Admin");
    });

    options.AddPolicy("AppAttestedSensitiveActionPolicy", policy =>
    {
        policy.AddAuthenticationSchemes("Cognito");
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new AppAttestationRequirement());
    });
});

var attestationMode = builder.Configuration["AppAttestation:EnforcementMode"] ?? "Enforce";
if (attestationMode is not ("ReportOnly" or "Enforce"))
{
    throw new InvalidOperationException("AppAttestation:EnforcementMode must be ReportOnly or Enforce.");
}
if (builder.Environment.IsProduction() && !string.Equals(attestationMode, "Enforce", StringComparison.Ordinal))
{
    throw new InvalidOperationException("App attestation must be enforced in production.");
}
if (builder.Environment.IsProduction() && !builder.Configuration.GetValue<bool>("AppAttestation:Enabled"))
{
    throw new InvalidOperationException("App attestation must be enabled in production.");
}

// Rate Limiter for OTP Endpoints
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("OtpPolicy", opt =>
    {
        opt.PermitLimit = 5;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });
});

// Controllers
builder.Services.AddControllers();

// OpenAPI / Swagger
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// JWT / Cognito Authentication setup
var cognitoAuthority = builder.Configuration["Cognito:Authority"];
var cognitoClientId = builder.Configuration["Cognito:UserPoolClientId"];
var useCognito = builder.Configuration.GetValue<bool?>("Cognito:UseCognito") ?? false;
var jwtKey = builder.Configuration["Jwt:Key"];
var jwtIssuer = builder.Configuration["Jwt:Issuer"];
var jwtAudience = builder.Configuration["Jwt:Audience"];

if (!useCognito || string.IsNullOrWhiteSpace(cognitoAuthority) || string.IsNullOrWhiteSpace(cognitoClientId))
{
    throw new InvalidOperationException("Cognito customer authentication must be enabled and configured with Authority and UserPoolClientId.");
}

if (!Uri.TryCreate(cognitoAuthority, UriKind.Absolute, out var cognitoAuthorityUri) || cognitoAuthorityUri.Scheme != Uri.UriSchemeHttps)
{
    throw new InvalidOperationException("Cognito:Authority must be an HTTPS absolute URI.");
}

if (string.IsNullOrWhiteSpace(jwtKey) || string.IsNullOrWhiteSpace(jwtIssuer) || string.IsNullOrWhiteSpace(jwtAudience))
{
    throw new InvalidOperationException("Jwt:Key, Jwt:Issuer, and Jwt:Audience must be configured for AdminJwt authentication.");
}

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = "Cognito";
    options.DefaultChallengeScheme = "Cognito";
})
.AddJwtBearer("Cognito", options =>
{
    options.Authority = cognitoAuthority;
    options.RequireHttpsMetadata = true;
    options.MapInboundClaims = false;

    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = cognitoAuthority,
        ValidateAudience = false,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        RoleClaimType = "cognito:groups",
        NameClaimType = "cognito:username"
    };

    options.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        },
        OnTokenValidated = async ctx =>
        {
            var clientId = ctx.Principal?.FindFirst("client_id")?.Value;
            if (string.IsNullOrWhiteSpace(clientId) || !string.Equals(clientId, cognitoClientId, StringComparison.Ordinal))
            {
                ctx.Fail("Token was not issued for this client.");
                return;
            }

            var tokenUse = ctx.Principal?.FindFirst("token_use")?.Value;
            if (!string.Equals(tokenUse, "access", StringComparison.Ordinal))
            {
                ctx.Fail("Only access tokens are accepted.");
                return;
            }

            var subject = ctx.Principal?.FindFirst("sub")?.Value;
            var username = ctx.Principal?.FindFirst("username")?.Value;
            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(username))
            {
                ctx.Fail("Cognito access token does not contain the required subject or username claim.");
                return;
            }

            var dbContext = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            var user = await dbContext.Users.FirstOrDefaultAsync(user => user.CognitoSubject == subject);
            if (user == null)
            {
                user = await dbContext.Users.FirstOrDefaultAsync(user => user.Email != null && user.Email.ToLower() == username.ToLower());
                if (user != null && string.IsNullOrWhiteSpace(user.CognitoSubject))
                {
                    user.CognitoSubject = subject;
                    user.ModifiedDate = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync();
                }
            }

            if (user == null)
            {
                ctx.Fail("No local profile is associated with this Cognito subject.");
                return;
            }

            ((ClaimsIdentity)ctx.Principal!.Identity!).AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        }
    };
})
.AddJwtBearer("AdminJwt", options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = jwtIssuer,
        ValidateAudience = true,
        ValidAudience = jwtAudience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey ?? string.Empty))
    };

    options.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
    };
});

// CORS setup for frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Auto-apply database migrations on startup
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        dbContext.Database.Migrate();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Database migrations failed during startup. The API will continue running, but database-backed endpoints may fail until the database is available.");
    }
}

var webRootPath = app.Environment.WebRootPath;
if (string.IsNullOrWhiteSpace(webRootPath))
{
    webRootPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
    app.Environment.WebRootPath = webRootPath;
}

var uploadFolder = app.Configuration["Uploads:ProfilePhotoFolder"] ?? "uploads/profile-photos";
Directory.CreateDirectory(Path.Combine(webRootPath, uploadFolder.TrimStart('/', '\\')));
app.Environment.WebRootFileProvider = new PhysicalFileProvider(webRootPath);

// Development Middleware
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    app.UseSwagger();
    app.UseSwaggerUI();
}

// Middleware
app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Routes
app.MapControllers();

// Health check endpoint
app.MapGet("/hello", () => "Hello World");

app.Run();
