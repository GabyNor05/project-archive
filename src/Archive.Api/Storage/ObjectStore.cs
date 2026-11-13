using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Archive.Api.Storage;

public sealed class StorageOptions
{
    /// <summary>S3 endpoint, e.g. https://s3.eu-central-003.backblazeb2.com or http://localhost:9000 (MinIO).</summary>
    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
}

/// <summary>
/// The ONLY place the app touches cloud storage. Everything else talks to this
/// interface, so swapping Backblaze for R2/MinIO/AWS is configuration, not code
/// (and tests could use an in-memory fake).
/// </summary>
public interface IObjectStore
{
    Task<Uri> PresignPutAsync(string key, string contentType, TimeSpan ttl);
    Task<Uri> PresignGetAsync(string key, TimeSpan ttl, string? downloadFileName = null);
    /// <summary>Returns the object's size, or null if it doesn't exist.</summary>
    Task<long?> GetSizeAsync(string key, CancellationToken ct);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);
    Task PutTextAsync(string key, string content, string contentType, CancellationToken ct);
}

public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private readonly AmazonS3Client _s3;
    private readonly StorageOptions _o;
    private readonly Protocol _protocol;

    public S3ObjectStore(IOptions<StorageOptions> options)
    {
        _o = options.Value;
        _protocol = _o.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTP : Protocol.HTTPS;

        var config = new AmazonS3Config
        {
            ServiceURL = _o.Endpoint,
            AuthenticationRegion = _o.Region,
            ForcePathStyle = true, // https://endpoint/bucket/key - works on every S3-compatible provider
            // Newer AWS SDKs add extra checksum headers by default that some S3-compatible
            // providers reject. Only send them when an operation requires it.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        _s3 = new AmazonS3Client(new BasicAWSCredentials(_o.AccessKeyId, _o.SecretAccessKey), config);
    }

    // A pre-signed URL is a normal URL with a signature and an expiry baked into the
    // query string. Whoever holds it can do exactly ONE thing (PUT this key / GET this
    // key) until it expires. The client uploads straight to storage, so a 2 GB .fig
    // never streams through (or times out) this API.
    public async Task<Uri> PresignPutAsync(string key, string contentType, TimeSpan ttl)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _o.Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            ContentType = contentType, // the client MUST send this exact Content-Type
            Expires = DateTime.UtcNow.Add(ttl),
            Protocol = _protocol,
        };
        return new Uri(await _s3.GetPreSignedURLAsync(request));
    }

    public async Task<Uri> PresignGetAsync(string key, TimeSpan ttl, string? downloadFileName = null)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _o.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(ttl),
            Protocol = _protocol,
        };
        if (downloadFileName is not null)
        {
            var safe = downloadFileName.Replace("\"", "");
            request.ResponseHeaderOverrides.ContentDisposition = $"attachment; filename=\"{safe}\"";
        }
        return new Uri(await _s3.GetPreSignedURLAsync(request));
    }

    public async Task<long?> GetSizeAsync(string key, CancellationToken ct)
    {
        try
        {
            var meta = await _s3.GetObjectMetadataAsync(_o.Bucket, key, ct);
            return meta.Headers.ContentLength;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        var response = await _s3.GetObjectAsync(_o.Bucket, key, ct);
        return response.ResponseStream; // disposing the stream releases the connection
    }

    public Task PutTextAsync(string key, string content, string contentType, CancellationToken ct) =>
        _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _o.Bucket,
            Key = key,
            ContentBody = content,
            ContentType = contentType,
        }, ct);

    public void Dispose() => _s3.Dispose();
}
