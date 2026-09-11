using Amazon.S3;
using Amazon.S3.Model;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public sealed class ListingMediaStorage : IListingMediaStorage
{
    private readonly IAmazonS3 _s3;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public ListingMediaStorage(IAmazonS3 s3, IConfiguration configuration, IWebHostEnvironment environment)
    {
        _s3 = s3;
        _configuration = configuration;
        _environment = environment;
    }

    public async Task<string> SaveAsync(
        int listingId,
        IFormFile file,
        string extension,
        CancellationToken cancellationToken = default)
    {
        var key = $"listing-media/{listingId}/{Guid.NewGuid():N}{extension}";
        if (_environment.IsDevelopment() || _environment.IsEnvironment("Testing"))
        {
            var path = ResolvePrivateLocalPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await file.CopyToAsync(destination, cancellationToken);
            return key;
        }

        var bucket = RequiredBucket();
        await using var input = file.OpenReadStream();
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = $"private/{key}",
            InputStream = input,
            ContentType = file.ContentType,
            AutoCloseStream = false
        }, cancellationToken);
        return key;
    }

    public async Task<Stream?> OpenReadAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        var key = NormalizeKey(storagePath);
        if (_environment.IsDevelopment() || _environment.IsEnvironment("Testing"))
        {
            var path = ResolvePrivateLocalPath(key);
            return File.Exists(path)
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                : null;
        }

        try
        {
            var response = await _s3.GetObjectAsync(RequiredBucket(), $"private/{key}", cancellationToken);
            return new S3ResponseStream(response);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        var key = NormalizeKey(storagePath);
        if (_environment.IsDevelopment() || _environment.IsEnvironment("Testing"))
        {
            var path = ResolvePrivateLocalPath(key);
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        await _s3.DeleteObjectAsync(RequiredBucket(), $"private/{key}", cancellationToken);
    }

    private string ResolvePrivateLocalPath(string key)
    {
        var configuredRoot = _configuration["Storage:PrivateMediaRoot"];
        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(_environment.ContentRootPath, "App_Data", "private-media")
            : configuredRoot);
        var path = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid media storage path.");
        return path;
    }

    private string RequiredBucket()
    {
        var bucket = _configuration["FileProcessor:S3BucketName"];
        return !string.IsNullOrWhiteSpace(bucket)
            ? bucket
            : throw new InvalidOperationException("Private media S3 bucket is not configured.");
    }

    private static string NormalizeKey(string storagePath)
    {
        var key = storagePath.Replace('\\', '/').TrimStart('/');
        if (key.Contains("..", StringComparison.Ordinal) || !key.StartsWith("listing-media/", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid media storage path.");
        return key;
    }

    private sealed class S3ResponseStream(GetObjectResponse response) : Stream
    {
        private Stream Inner => response.ResponseStream;
        public override bool CanRead => Inner.CanRead;
        public override bool CanSeek => Inner.CanSeek;
        public override bool CanWrite => Inner.CanWrite;
        public override long Length => Inner.Length;
        public override long Position { get => Inner.Position; set => Inner.Position = value; }
        public override void Flush() => Inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => Inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);
        public override void SetLength(long value) => Inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => Inner.WriteAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) response.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await Inner.DisposeAsync();
            response.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
