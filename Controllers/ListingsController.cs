using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.DTOs.Inventory;
using PropSeekr.DTOs.Matches;
using PropSeekr.Models;
using PropSeekr.Services;
using PropSeekr.Services.Interfaces;

using PropSeekr.Attributes;

namespace PropSeekr.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/listings")]
public class ListingsController : ControllerBase
{
    private static readonly IReadOnlyDictionary<string, string> AllowedMediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = ".jpg",
            ["image/png"] = ".png",
            ["image/webp"] = ".webp",
            ["video/mp4"] = ".mp4",
            ["video/quicktime"] = ".mov",
            ["video/webm"] = ".webm"
        };
    private readonly AppDbContext _dbContext;
    private readonly IBrokerIdentityService _brokerIdentityService;
    private readonly IBrokerListingsService _brokerListingsService;
    private readonly IMatchingPipelineService _matchingPipeline;
    private readonly ILogger<ListingsController> _logger;

    public ListingsController(
        AppDbContext dbContext,
        IBrokerIdentityService brokerIdentityService,
        IBrokerListingsService brokerListingsService,
        IMatchingPipelineService matchingPipeline,
        ILogger<ListingsController> logger)
    {
        _dbContext = dbContext;
        _brokerIdentityService = brokerIdentityService;
        _brokerListingsService = brokerListingsService;
        _matchingPipeline = matchingPipeline;
        _logger = logger;
    }

    [HttpGet("mine")]
    [ProducesResponseType(typeof(GetBrokerListingsResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyListings(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? transactionType = null,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || limit is < 1 or > 100)
        {
            return BadRequest(new
            {
                success = false,
                message = "page must be at least 1 and limit must be between 1 and 100."
            });
        }

        try
        {
            if (User.IsInRole("Admin"))
            {
                return Ok(await _brokerListingsService.GetAllListingsAsync(
                    page, limit, transactionType, status, cancellationToken));
            }

            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(userIdClaim, out var userId))
            {
                return Unauthorized(new { success = false, message = "Invalid authenticated user." });
            }

            var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId, cancellationToken);
            if (!brokerId.HasValue)
            {
                return NotFound(new
                {
                    success = false,
                    code = "broker_profile_not_linked",
                    message = "No broker profile is linked to this account."
                });
            }

            var response = await _brokerListingsService.GetMyListingsAsync(
                brokerId.Value,
                page,
                limit,
                transactionType,
                status,
                cancellationToken);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> CreateListing([FromBody] CreateListingRequestDto request)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { success = false, message = "Invalid authenticated user." });
        }

        var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId);
        if (!brokerId.HasValue)
        {
            return NotFound(new
            {
                success = false,
                code = "broker_profile_not_linked",
                message = "No broker profile is linked to this account."
            });
        }

        request.BrokerId = brokerId.Value;
        return await SaveListingInternal(request, request.Source ?? "manual");
    }

    [HttpPost("whatsapp-intake")]
    [RequireInternalServiceKey]
    public async Task<IActionResult> WhatsappIntake([FromBody] CreateListingRequestDto request)
    {
        return await SaveListingInternal(request, "whatsapp");
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetListingDetails([FromRoute] int id)
    {
        var listing = await _dbContext.Listings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id);
        if (listing == null)
        {
            return NotFound(new { success = false, message = "Listing not found." });
        }

        var sizes = await _dbContext.ListingSizes.AsNoTracking()
            .Where(ls => ls.ListingId == id)
            .Select(ls => new { size_sqft = ls.SizeSqft, size_label = ls.SizeLabel })
            .ToListAsync();

        var requirements = await _dbContext.ListingRequirements.AsNoTracking()
            .Where(lr => lr.ListingId == id)
            .Select(lr => new
            {
                requirement_id = lr.RequirementId,
                requirement_type = lr.Requirement != null ? lr.Requirement.RequirementType : "rent",
                property_type = lr.Requirement != null ? lr.Requirement.PropertyType : null,
                budget = lr.Requirement != null ? lr.Requirement.Budget : null,
                budget_unit = lr.Requirement != null ? lr.Requirement.BudgetUnit : null,
                size = lr.Requirement != null ? lr.Requirement.Size : null,
                status = lr.Requirement != null ? lr.Requirement.Status : null,
                match_status = lr.MatchStatus,
                match_score = lr.MatchScore
            })
            .ToListAsync();

        return Ok(new
        {
            success = true,
            data = new
            {
                listing_id = listing.Id,
                broker_id = listing.BrokerId,
                property_type = listing.PropertyType,
                locality = listing.ProjectName ?? "N/A", // Map project_name back to locality
                price = listing.Price,
                status = listing.Status,
                source = listing.Source,
                raw_message_text = listing.RawMessageText,
                posted_by = listing.PostedBy ?? "BROKER",
                created_at = listing.CreatedAt,
                updated_at = listing.UpdatedAt,
                sizes = sizes,
                requirements = requirements
            }
        });
    }

    [HttpPost("{id}/media")]
    [RequestSizeLimit(160_000_000)]
    public async Task<IActionResult> UploadListingMedia(
        [FromRoute] int id,
        [FromForm] List<IFormFile> files,
        [FromServices] IWebHostEnvironment environment,
        [FromServices] IConfiguration configuration)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized(new { success = false, message = "Invalid authenticated user." });

        var listing = await _dbContext.Listings.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id);
        if (listing is null) return NotFound(new { success = false, message = "Listing not found." });

        var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId);
        if (!User.IsInRole("Admin") && brokerId != listing.BrokerId) return Forbid();
        if (files.Count == 0) return BadRequest(new { success = false, message = "Select at least one photo or video." });

        var existingCount = await _dbContext.ListingMedia.CountAsync(item => item.ListingId == id);
        var maxCount = configuration.GetValue("Uploads:MaxListingMediaCount", 12);
        if (existingCount + files.Count > maxCount)
            return BadRequest(new { success = false, message = $"A listing can have at most {maxCount} photos and videos." });

        var maxImageBytes = configuration.GetValue<long>("Uploads:MaxListingImageSizeInBytes", 10 * 1024 * 1024);
        var maxVideoBytes = configuration.GetValue<long>("Uploads:MaxListingVideoSizeInBytes", 100 * 1024 * 1024);
        foreach (var file in files)
        {
            if (file.Length <= 0 || !AllowedMediaTypes.TryGetValue(file.ContentType, out _))
                return BadRequest(new { success = false, message = "Only JPG, PNG, WEBP, MP4, MOV and WEBM media are allowed." });
            if (!await HasValidMediaSignatureAsync(file))
                return BadRequest(new { success = false, message = $"{file.FileName} does not match its declared media type." });
            var maximum = file.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? maxVideoBytes : maxImageBytes;
            if (file.Length > maximum)
                return BadRequest(new { success = false, message = $"{file.FileName} exceeds the allowed file size." });
        }

        var webRoot = environment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot)) webRoot = Path.Combine(environment.ContentRootPath, "wwwroot");
        var relativeFolder = Path.Combine("uploads", "listing-media", id.ToString());
        var uploadFolder = Path.Combine(webRoot, relativeFolder);
        Directory.CreateDirectory(uploadFolder);

        var createdFiles = new List<string>();
        try
        {
            var nextSortOrder = existingCount;
            var createdMedia = new List<ListingMedia>();
            foreach (var file in files)
            {
                var extension = AllowedMediaTypes[file.ContentType];
                var fileName = $"{Guid.NewGuid():N}{extension}";
                var filePath = Path.Combine(uploadFolder, fileName);
                await using (var stream = new FileStream(filePath, FileMode.CreateNew))
                {
                    await file.CopyToAsync(stream);
                }
                createdFiles.Add(filePath);

                var media = new ListingMedia
                {
                    ListingId = id,
                    MediaType = file.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? "video" : "image",
                    StoragePath = Path.Combine(relativeFolder, fileName),
                    OriginalFileName = Path.GetFileName(file.FileName),
                    MimeType = file.ContentType,
                    FileSizeBytes = file.Length,
                    SortOrder = nextSortOrder++,
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.ListingMedia.Add(media);
                createdMedia.Add(media);
            }

            await _dbContext.SaveChangesAsync();
            return Ok(new
            {
                success = true,
                listing_id = id,
                media = createdMedia.Select(item => new
                {
                    media_id = item.Id,
                    media_type = item.MediaType,
                    mime_type = item.MimeType,
                    sort_order = item.SortOrder
                })
            });
        }
        catch
        {
            foreach (var filePath in createdFiles)
            {
                if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath);
            }
            throw;
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetListings([FromQuery] string? postedBy, [FromQuery] int page = 1, [FromQuery] int limit = 20)
    {
        if (page <= 0) page = 1;
        if (limit <= 0) limit = 20;

        var query = _dbContext.Listings.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(postedBy))
        {
            var filter = postedBy.ToUpperInvariant();
            query = query.Where(l => l.PostedBy == filter);
        }

        var totalCount = await query.CountAsync();

        var listings = await query
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .Select(l => new
            {
                listing_id = l.Id,
                broker_id = l.BrokerId,
                property_type = l.PropertyType,
                locality = l.ProjectName ?? "N/A",
                price = l.Price,
                status = l.Status,
                source = l.Source,
                posted_by = l.PostedBy ?? "BROKER",
                created_at = l.CreatedAt,
                requirement_count = _dbContext.ListingRequirements.Count(lr => lr.ListingId == l.Id)
            })
            .ToListAsync();

        return Ok(new
        {
            success = true,
            total = totalCount,
            page = page,
            limit = limit,
            data = listings
        });
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> PatchListing([FromRoute] int id, [FromBody] CreateListingRequestDto request)
    {
        var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.Id == id);
        if (listing == null)
        {
            return NotFound(new { success = false, message = "Listing not found." });
        }

        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { success = false, message = "Invalid authenticated user." });
        }

        var isAdmin = User.IsInRole("Admin");
        var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId);

        if (!isAdmin && (!brokerId.HasValue || listing.BrokerId != brokerId.Value))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                success = false,
                message = "You can only update your own listings."
            });
        }

        // Apply fields if supplied in patch payload
        if (request.PropertyType != null)
        {
            listing.PropertyType = request.PropertyType;
        }

        if (request.Locality != null)
        {
            listing.ProjectName = request.Locality;
        }

        if (request.City != null)
        {
            listing.City = request.City;
        }

        var effectiveCity = request.City ?? listing.City;
        var effectiveLocality = request.Locality ?? listing.ProjectName;
        if (request.Latitude.HasValue && request.Longitude.HasValue &&
            !string.IsNullOrWhiteSpace(effectiveCity) && !string.IsNullOrWhiteSpace(effectiveLocality))
        {
            listing.MasterId = await MasterLocationResolver.ResolveAsync(
                _dbContext,
                effectiveCity,
                effectiveLocality,
                request.Latitude.Value,
                request.Longitude.Value);
        }

        if (request.Price.HasValue)
        {
            listing.Price = request.Price;
        }

        if (request.Status != null)
        {
            listing.Status = request.Status;
        }

        // Reset freshness timestamps on update
        listing.FreshnessUpdatedAt = DateTime.UtcNow;
        listing.LastRefreshedAt = DateTime.UtcNow;
        listing.UpdatedAt = DateTime.UtcNow;

        _dbContext.Listings.Update(listing);

        // Update sizes if provided
        if (request.Sizes != null)
        {
            var existingSizes = await _dbContext.ListingSizes.Where(ls => ls.ListingId == id).ToListAsync();
            _dbContext.ListingSizes.RemoveRange(existingSizes);

            foreach (var size in request.Sizes)
            {
                var newSize = new ListingSize
                {
                    ListingId = listing.Id,
                    SizeSqft = size.SizeSqft,
                    SizeLabel = size.Bhk.HasValue ? $"{size.Bhk} BHK" : "Flat"
                };
                _dbContext.ListingSizes.Add(newSize);
            }
        }

        if (request.Details.HasValue || request.PhotoSharingPreference != null)
        {
            var detail = await _dbContext.ListingDetails.SingleOrDefaultAsync(item => item.ListingId == id);
            detail ??= new ListingDetail { ListingId = id, CreatedAt = DateTime.UtcNow };
            if (request.Details.HasValue) detail.DetailsJson = ValidateAndSerializeDetails(request.Details);
            if (request.PhotoSharingPreference != null) detail.PhotoSharingPreference = NormalizePhotoPreference(request.PhotoSharingPreference);
            detail.UpdatedAt = DateTime.UtcNow;
            if (_dbContext.Entry(detail).State == EntityState.Detached) _dbContext.ListingDetails.Add(detail);
        }

        await _dbContext.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = "Listing updated successfully.",
            listing_id = listing.Id
        });
    }

    private async Task<IActionResult> SaveListingInternal(CreateListingRequestDto request, string source)
    {
        if (request.BrokerId <= 0)
        {
            return BadRequest(new { success = false, message = "Valid broker_id is required." });
        }

        var brokerExists = await _dbContext.Brokers.AnyAsync(b => b.Id == request.BrokerId);
        if (!brokerExists)
        {
            return NotFound(new { success = false, message = $"Broker ID {request.BrokerId} not found." });
        }

        var normalizedListingType = request.ListingType?.Trim().ToUpperInvariant();
        if (normalizedListingType == "RENTAL") normalizedListingType = "RENT";
        if (normalizedListingType == "SALE") normalizedListingType = "SELL";
        if (source == "manual" && normalizedListingType is not ("RENT" or "SELL" or "LEASE"))
        {
            return BadRequest(new
            {
                success = false,
                message = "listing_type is required and must be RENT or SELL."
            });
        }

        if (source == "manual" &&
            (string.IsNullOrWhiteSpace(request.City) ||
             string.IsNullOrWhiteSpace(request.Locality) ||
             !request.Latitude.HasValue ||
             !request.Longitude.HasValue ||
             request.Latitude is < -90 or > 90 ||
             request.Longitude is < -180 or > 180 ||
             (request.Latitude == 0 && request.Longitude == 0)))
        {
            return BadRequest(new
            {
                success = false,
                message = "City, locality, and valid property coordinates are required."
            });
        }

        using var transaction = await _dbContext.Database.BeginTransactionAsync();
        try
        {
            var masterId = request.MasterId;
            if (request.Latitude.HasValue && request.Longitude.HasValue &&
                !string.IsNullOrWhiteSpace(request.City) && !string.IsNullOrWhiteSpace(request.Locality))
            {
                masterId = await MasterLocationResolver.ResolveAsync(
                    _dbContext,
                    request.City,
                    request.Locality,
                    request.Latitude.Value,
                    request.Longitude.Value);
            }

            var listing = new Listing
            {
                BrokerId = request.BrokerId,
                MasterId = masterId,
                Source = source,
                RawMessageText = BuildListingMatchText(request),
                ListingType = normalizedListingType ?? "SELL",
                PropertyType = InventoryNormalization.PropertyType(request.PropertyType),
                Configuration = InventoryNormalization.Configuration(request.Configuration),
                Price = request.Price,
                PriceUnit = NormalizePriceUnit(request.PriceUnit),
                Size = request.Size,
                Furnishing = InventoryNormalization.Furnishing(request.Furnishing),
                Facing = InventoryNormalization.Facing(request.Facing),
                FloorNumber = request.FloorNumber,
                Status = request.Status ?? "active",
                ExpiresAt = request.MessageDatetime?.AddDays(30) ?? DateTime.UtcNow.AddDays(30),
                LastRefreshedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                FreshnessUpdatedAt = DateTime.UtcNow,
                ProjectName = request.ProjectName ?? request.Locality,
                RoadInfo = request.RoadInfo,
                ContentHash = request.ContentHash,
                GroupName = request.GroupName,
                MessageDatetime = request.MessageDatetime ?? DateTime.UtcNow,
                PriceStatus = request.PriceStatus,
                City = request.City,
                PostedBy = request.PostedBy ?? "BROKER"
            };

            _dbContext.Listings.Add(listing);
            await _dbContext.SaveChangesAsync(); // Auto-generates listing ID

            if (request.Details.HasValue || !string.IsNullOrWhiteSpace(request.PhotoSharingPreference))
            {
                _dbContext.ListingDetails.Add(new ListingDetail
                {
                    ListingId = listing.Id,
                    DetailsJson = ValidateAndSerializeDetails(request.Details),
                    PhotoSharingPreference = NormalizePhotoPreference(request.PhotoSharingPreference),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
                await _dbContext.SaveChangesAsync();
            }

            if (request.Sizes != null && request.Sizes.Any())
            {
                foreach (var size in request.Sizes)
                {
                    var listingSize = new ListingSize
                    {
                        ListingId = listing.Id,
                        SizeSqft = size.SizeSqft,
                        SizeLabel = size.Bhk.HasValue ? $"{size.Bhk} BHK" : "Flat"
                    };
                    _dbContext.ListingSizes.Add(listingSize);
                }
                await _dbContext.SaveChangesAsync();
            }

            if (request.RequirementIds != null && request.RequirementIds.Any())
            {
                var existingReqIds = await _dbContext.Requirements
                    .Where(r => request.RequirementIds.Contains(r.Id))
                    .Select(r => r.Id)
                    .ToListAsync();

                foreach (var reqId in existingReqIds)
                {
                    var map = new ListingRequirement
                    {
                        ListingId = listing.Id,
                        RequirementId = reqId,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _dbContext.ListingRequirements.Add(map);
                }
                await _dbContext.SaveChangesAsync();
            }

            await transaction.CommitAsync();

            IReadOnlyList<int> matches = [];
            var embeddingCompleted = true;
            try
            {
                await _matchingPipeline.TriggerForListingAsync(listing.Id);
                matches = await _dbContext.Matches
                    .AsNoTracking()
                    .Where(match => match.ListingId == listing.Id && match.Status == "MATCHED")
                    .Select(match => match.Id)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                embeddingCompleted = false;
                _logger.LogError(ex, "Embedding and matching pipeline failed for listing {ListingId}", listing.Id);
            }

            return Ok(new
            {
                success = true,
                listing_id = listing.Id,
                match_count = matches.Count,
                embedding_completed = embeddingCompleted,
                message = embeddingCompleted
                    ? "Listing created successfully. Gemini embedding and matching completed."
                    : "Listing created, but Gemini embedding or matching failed. Check API logs and retry the embedding."
            });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    private static string? NormalizePriceUnit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "TOTAL";
        return value.Trim().ToUpperInvariant() switch
        {
            "INR" or "TOTAL" => "TOTAL",
            "PER MONTH" or "PER_MONTH" => "PER_MONTH",
            "PER SQFT" or "PER_SQFT" => "PER_SQFT",
            _ => value.Trim().ToUpperInvariant()
        };
    }

    private static string BuildListingMatchText(CreateListingRequestDto request)
    {
        var parts = new[]
        {
            request.RawMessageText, request.ListingType, request.Configuration,
            request.PropertyType, request.ProjectName ?? request.Locality, request.City,
            request.Size.HasValue ? $"{request.Size.Value} sqft" : null,
            request.Price.HasValue ? $"price {request.Price.Value} {NormalizePriceUnit(request.PriceUnit)}" : null,
            request.Furnishing, request.Facing, request.RoadInfo
        };
        return string.Join(". ", parts.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string ValidateAndSerializeDetails(System.Text.Json.JsonElement? details)
    {
        if (!details.HasValue || details.Value.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined)
            return "{}";
        if (details.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new ArgumentException("details must be a JSON object.");

        var serialized = details.Value.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(serialized) > 32 * 1024)
            throw new ArgumentException("Listing details cannot exceed 32 KB.");
        return serialized;
    }

    private static string? NormalizePhotoPreference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "share freely" => "SHARE_FREELY",
            "on request" => "ON_REQUEST",
            "no photos" => "NO_PHOTOS",
            "share_freely" or "on_request" or "no_photos" => value.Trim().ToUpperInvariant(),
            _ => throw new ArgumentException("photo_sharing_preference is invalid.")
        };
    }

    private static async Task<bool> HasValidMediaSignatureAsync(IFormFile file)
    {
        var header = new byte[12];
        await using var stream = file.OpenReadStream();
        var read = await stream.ReadAsync(header);
        if (read < 4) return false;

        return file.ContentType.ToLowerInvariant() switch
        {
            "image/jpeg" => header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            "image/png" => read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            "image/webp" => read >= 12 && System.Text.Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(header, 8, 4) == "WEBP",
            "video/mp4" or "video/quicktime" => read >= 8 && System.Text.Encoding.ASCII.GetString(header, 4, 4) == "ftyp",
            "video/webm" => header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3,
            _ => false
        };
    }
}
