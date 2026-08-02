using System.Text;
using System.Text.Json;
using System.Linq;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using PropSeekr.Data;
using PropSeekr.Services;
using PropSeekr.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

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

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        connectionString,
        o => o.UseNetTopologySuite()));

// Services
builder.Services.AddHttpClient();
builder.Services.AddScoped<IOtpDeliveryService, Msg91OtpDeliveryService>();
builder.Services.AddScoped<IEmailService, AmazonSesEmailService>();
builder.Services.AddScoped<IEmailOtpService, EmailOtpService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IProfileService, ProfileService>();
builder.Services.AddScoped<ISearchPropertyService, SearchPropertyService>();
builder.Services.AddScoped<IRequirementService, RequirementService>();
builder.Services.AddScoped<IPropertyInventoryService, PropertyInventoryService>();
builder.Services.AddScoped<IRazorpayService, RazorpayService>();
builder.Services.AddScoped<IUserMatchesService, UserMatchesService>();
builder.Services.AddScoped<INotificationService, NotificationService>();

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

// JWT Authentication setup
var jwtKey = builder.Configuration["Jwt:Key"];
if (!string.IsNullOrEmpty(jwtKey))
{
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
    dbContext.Database.Migrate();
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

// Enable Swagger globally for testing and API documentation
app.MapOpenApi();
app.UseSwagger();
app.UseSwaggerUI();

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

