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
using PropSeekr.FileProcessing;

var builder = WebApplication.CreateBuilder(args);

// Load all secrets from AWS Secrets Manager config if configured
var secretsManagerConfigName = builder.Configuration["AWS:SecretsManagerConfigName"];
if (!string.IsNullOrWhiteSpace(secretsManagerConfigName))
{
    var secretString = GetSecretFromAgentOrSdk(secretsManagerConfigName, builder.Configuration);
    if (secretString != null)
    {
        try
        {
            using var doc = JsonDocument.Parse(secretString);
            var root = doc.RootElement;
            var secretsDict = new Dictionary<string, string?>();
            foreach (var prop in root.EnumerateObject())
            {
                var val = prop.Value.ValueKind == JsonValueKind.String 
                    ? prop.Value.GetString()?.Trim() 
                    : prop.Value.GetRawText()?.Trim();
                
                secretsDict[prop.Name] = val;
                
                // Also set environment variables directly so that unchanged Lambda code finds them
                if (!string.IsNullOrEmpty(val))
                {
                    Environment.SetEnvironmentVariable(prop.Name, val);
                }
            }
            builder.Configuration.AddInMemoryCollection(secretsDict);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Secrets Manager Parse Error] Failed to parse secrets JSON: {ex.Message}");
        }
    }
}

// The migrated processor retains the Lambda's proven configuration names.
// This lets FileProcessor:* app settings work locally while deployment
// environment variables remain the preferred production configuration.
FileProcessorConfigurationBridge.Apply(builder.Configuration);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

var dbHost = Environment.GetEnvironmentVariable("DB_HOST")?.Trim();
var dbPort = Environment.GetEnvironmentVariable("DB_PORT")?.Trim();
var dbName = Environment.GetEnvironmentVariable("DB_NAME")?.Trim();
var dbUser = Environment.GetEnvironmentVariable("DB_USERNAME")?.Trim();
var dbPass = Environment.GetEnvironmentVariable("DB_PASSWORD")?.Trim();

if (!string.IsNullOrWhiteSpace(dbHost))
{
    connectionString = $"Host={dbHost};Port={dbPort ?? "5432"};Database={dbName ?? "postgres"};Username={dbUser ?? "postgres"};Password={dbPass}";
}
else
{
    var secretName = builder.Configuration["AWS:DatabaseSecretName"];
    if (!string.IsNullOrWhiteSpace(secretName))
    {
        var secretString = GetSecretFromAgentOrSdk(secretName, builder.Configuration);
        if (secretString != null)
        {
            string? dbPassword = null;
            try
            {
                using var doc = JsonDocument.Parse(secretString);
                var root = doc.RootElement;
                if (root.TryGetProperty("password", out var pwdProp))
                {
                    dbPassword = pwdProp.GetString()?.Trim();
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
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(
        connectionString,
        o => o.UseNetTopologySuite()));

// Services
builder.Services.AddHttpClient();
builder.Services.AddSingleton<FileProcessorHost>();
builder.Services.AddScoped<IOtpDeliveryService, Msg91OtpDeliveryService>();
builder.Services.AddScoped<IEmailService, AmazonSesEmailService>();
builder.Services.AddScoped<IEmailOtpService, EmailOtpService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IProfileService, ProfileService>();
builder.Services.AddScoped<ISearchPropertyService, SearchPropertyService>();
builder.Services.AddScoped<IRequirementService, RequirementService>();
builder.Services.AddScoped<IBrokerInventoryService, BrokerInventoryService>();
builder.Services.AddScoped<IRazorpayService, RazorpayService>();
builder.Services.AddScoped<IUserMatchesService, UserMatchesService>();
builder.Services.AddScoped<IUnlockService, UnlockService>();
builder.Services.AddScoped<IBrokerIdentityService, BrokerIdentityService>();
builder.Services.AddScoped<IBrokerListingsService, BrokerListingsService>();
builder.Services.AddScoped<IAutomatedMatchingService, AutomatedMatchingService>();
builder.Services.AddScoped<IMatchingPipelineService, MatchingPipelineService>();
builder.Services.AddScoped<IEmbeddingJobService, EmbeddingJobService>();
builder.Services.AddScoped<MatchInvalidationService>();
builder.Services.AddHostedService<EmbeddingJobWorker>();

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

// Production migrations are explicit; an API restart must not mutate the database.
if (builder.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.Migrate();

    var scriptPath = Path.Combine(app.Environment.ContentRootPath, "scripts", "harden-matching-engine.sql");
    if (File.Exists(scriptPath))
    {
        var sql = File.ReadAllText(scriptPath);
        dbContext.Database.ExecuteSqlRaw(sql);
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

static string? GetSecretFromAgentOrSdk(string secretName, IConfiguration configuration)
{
    // Try AWS Secrets Manager Agent (Local HTTP Service) first
    const string tokenPath = "/var/run/awssmatoken";
    if (File.Exists(tokenPath))
    {
        try
        {
            var token = File.ReadAllText(tokenPath).Trim();
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("X-Aws-Parameters-Secrets-Token", token);
            
            var url = $"http://localhost:2773/secretsmanager/get?secretId={Uri.EscapeDataString(secretName)}";
            var httpResponse = client.GetAsync(url).GetAwaiter().GetResult();
            if (httpResponse.IsSuccessStatusCode)
            {
                var content = httpResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("SecretString", out var secretStrProp))
                {
                    return secretStrProp.GetString();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Secrets Manager Agent Warning] Failed to fetch via agent on localhost:2773. Error: {ex.Message}");
        }
    }

    // Fallback to standard AWS SDK
    try
    {
        var region = configuration["AWS:Region"] ?? "ap-south-1";
        var accessKey = configuration["AWS:AccessKeyId"];
        var secretKey = configuration["AWS:SecretAccessKey"];

        IAmazonSecretsManager secretsClient = string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey)
            ? new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region))
            : new AmazonSecretsManagerClient(accessKey, secretKey, RegionEndpoint.GetBySystemName(region));

        var request = new GetSecretValueRequest { SecretId = secretName };
        var sdkResponse = secretsClient.GetSecretValueAsync(request).GetAwaiter().GetResult();
        return sdkResponse?.SecretString;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Secrets Manager SDK Error] Failed to fetch via SDK: {ex.Message}");
    }

    return null;
}
