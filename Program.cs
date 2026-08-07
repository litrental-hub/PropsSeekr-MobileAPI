using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using PropSeekr.Data;
using PropSeekr.Services;
using PropSeekr.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Database
<<<<<<< Updated upstream
=======
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
Console.WriteLine("=================================");
Console.WriteLine("Connection String:");
Console.WriteLine(connectionString);
Console.WriteLine("=================================");
if (connectionString != null)
{
    connectionString = connectionString.Replace("]QI[:c[scyzMBo?a)1c_FB-xQw<0", "aman_anshul");
}

var secretName = builder.Configuration["AWS:DatabaseSecretName"];
if (!string.IsNullOrWhiteSpace(secretName))
{
    try
    {
        var region = builder.Configuration["AWS:Region"] ?? "ap-south-1";
        var accessKey = builder.Configuration["AWS:AccessKeyId"];
        var secretKey = builder.Configuration["AWS:SecretAccessKey"];

        IAmazonSecretsManager secretsClient;
        if (!string.IsNullOrWhiteSpace(accessKey) && !string.IsNullOrWhiteSpace(secretKey))
        {
            secretsClient = new AmazonSecretsManagerClient(accessKey, secretKey, RegionEndpoint.GetBySystemName(region));
        }
        else
        {
            secretsClient = new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region));
        }

        var request = new GetSecretValueRequest { SecretId = secretName };
        var response = secretsClient.GetSecretValueAsync(request).GetAwaiter().GetResult();
        if (response?.SecretString != null)
        {
            var secretString = response.SecretString;
            string? dbPassword = null;
            try
            {
                using var doc = JsonDocument.Parse(secretString);
                var root = doc.RootElement;
                if (root.TryGetProperty("password", out var pwdProp))
                {
                    dbPassword = pwdProp.GetString();
                
                }
                else if (root.TryGetProperty("ConnectionString", out var connProp))
                {
                    connectionString = connProp.GetString();
                }
            }
            catch
            {
                dbPassword = secretString;
            }

            if (!string.IsNullOrWhiteSpace(dbPassword) && connectionString != null)
            {
                var connBuilder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString)
                {
                    Password = dbPassword
                };
                connectionString = connBuilder.ToString();
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Secrets Manager Error] Failed to fetch database credentials: {ex.Message}");
    }
}

>>>>>>> Stashed changes
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        o => o.UseNetTopologySuite()));

// Services
builder.Services.AddHttpClient();
builder.Services.AddScoped<IOtpDeliveryService, Msg91OtpDeliveryService>();
builder.Services.AddScoped<IEmailService, AmazonSesEmailService>();
builder.Services.AddScoped<IEmailOtpService, EmailOtpService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IProfileService, ProfileService>();
builder.Services.AddScoped<ISearchPropertyService, SearchPropertyService>();
builder.Services.AddScoped<IPropertyInventoryService, PropertyInventoryService>();
builder.Services.AddScoped<IRazorpayService, RazorpayService>();
builder.Services.AddScoped<IUserMatchesService, UserMatchesService>();

builder.Services.AddAuthorization();

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

if (useCognito && !string.IsNullOrEmpty(cognitoAuthority) && !string.IsNullOrEmpty(cognitoClientId))
{
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    }).AddJwtBearer(options =>
    {
        options.Authority = cognitoAuthority;
        options.Audience = cognitoClientId;
        options.RequireHttpsMetadata = true;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = cognitoAuthority,
            ValidateAudience = true,
            ValidAudience = cognitoClientId,
            ValidateLifetime = true,
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
            OnTokenValidated = ctx =>
            {
                // Additional app-specific token checks could be done here,
                // e.g. verify custom claim presence, enforce scopes, etc.
                return Task.CompletedTask;
            }
        };
    });
}
else if (!string.IsNullOrEmpty(jwtKey))
{
    // Fallback to legacy symmetric key JWT validation (kept for compatibility/testing)
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    }).AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });
}

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
