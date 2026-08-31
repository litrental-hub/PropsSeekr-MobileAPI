using System.Text;
using System.Linq;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using PropSeekr.Data;
using PropSeekr.Services;
using PropSeekr.Services.Interfaces;
using PropSeekr.FileProcessing;
using PropSeekr.Configuration;

var builder = WebApplication.CreateBuilder(args);

AwsSecretsConfigurationLoader.Load(builder);

// The migrated processor retains the Lambda's proven configuration names.
// This bridges the AWS-backed FileProcessor:* configuration into the names
// expected by the vendored processor.
FileProcessorConfigurationBridge.Apply(builder.Configuration);
if (builder.Environment.IsDevelopment())
{
    // The vendored processor resolves local files from an environment variable.
    // Use the same absolute directory as the authenticated upload endpoint so
    // launching the API from a different working directory cannot break it.
    Environment.SetEnvironmentVariable(
        "LOCAL_BULK_IMPORT_DIRECTORY",
        LocalBulkImportStorage.GetDirectory(builder.Configuration, builder.Environment));
}

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

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
builder.Services.AddHostedService<BulkImportJobWorker>();
builder.Services.AddHostedService<LocationRemediationWorker>();

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
