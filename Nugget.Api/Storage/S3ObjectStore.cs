using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Amazon.S3.Util;
using Nugget.Api.Configuration;

namespace Nugget.Api.Storage;

/// <summary>S3 compatible object storage (MinIO, Ceph, AWS, ...).</summary>
/// <remarks>
///   TLS certificates are validated against the OS trust store, so an
///   internal CA has to be installed there (see the Dockerfile).
/// </remarks>
public sealed class S3ObjectStore : IObjectStore, IDisposable {
  private const long C_PART_SIZE_ = 16L * 1024 * 1024;
  private const int C_DELETE_BATCH_ = 1000;
  private const string C_META_PREFIX_ = "x-amz-meta-";

  private readonly AmazonS3Client client_;
  private readonly ILogger<S3ObjectStore> logger_;
  private readonly StorageOptions options_;
  private readonly string prefix_;

  public S3ObjectStore(StorageOptions options, ILogger<S3ObjectStore> logger) {
    if (string.IsNullOrWhiteSpace(options.Bucket)) {
      throw new InvalidOperationException("Storage:Bucket must be set.");
    }

    options_ = options;
    logger_ = logger;
    string prefix = options.Prefix.Trim('/');
    prefix_ = prefix.Length == 0 ? "" : prefix + "/";

    AmazonS3Config config = new() {
      ForcePathStyle = options.ForcePathStyle,
      // The SDK's default flexible checksums (trailing CRC headers) are not
      // understood by older MinIO releases and other S3 compatible stores.
      RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
      ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
    };
    if (string.IsNullOrWhiteSpace(options.ServiceUrl)) {
      config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
    } else {
      config.ServiceURL = options.ServiceUrl;
      config.AuthenticationRegion = options.Region;
    }

    client_ = string.IsNullOrEmpty(options.AccessKey)
                ? new AmazonS3Client(config)
                : new AmazonS3Client(
                  new BasicAWSCredentials(
                    options.AccessKey,
                    options.SecretKey ?? ""
                  ),
                  config
                );
  }

  public string Description =>
    $"s3 ({options_.ServiceUrl ?? "aws:" + options_.Region}/" +
    $"{options_.Bucket}/{prefix_})";

  public void Dispose() {
    client_.Dispose();
  }

  public async Task InitializeAsync(CancellationToken ct) {
    if (await AmazonS3Util.DoesS3BucketExistV2Async(client_, options_.Bucket)) {
      return;
    }

    if (!options_.CreateBucket) {
      throw new InvalidOperationException(
        $"Bucket '{options_.Bucket}' does not exist " +
        "(and Storage:CreateBucket is false)."
      );
    }

    logger_.LogInformation("Creating bucket {Bucket}", options_.Bucket);
    PutBucketRequest request = new() {
      BucketName = options_.Bucket,
      // The SDK would otherwise send the client's region as location
      // constraint, which servers in another (or no) region reject.
      UseClientRegion = false
    };
    // us-east-1 is the default and must not be sent as a location
    // constraint; strict implementations (AWS, RustFS) reject it.
    if (!string.Equals(
          options_.Region,
          "us-east-1",
          StringComparison.OrdinalIgnoreCase
        )) {
      request.BucketRegionName = options_.Region;
    }

    await client_.PutBucketAsync(request, ct);
  }

  public async Task<StoredObject?> GetAsync(string key, CancellationToken ct) {
    await using ObjectReadStream? stream = await OpenReadAsync(key, ct);
    if (stream == null) {
      return null;
    }

    using MemoryStream buffer = new(
      (int)Math.Clamp(stream.Info.Size, 0, int.MaxValue)
    );
    await stream.Stream.CopyToAsync(buffer, ct);
    return new StoredObject {
      Info = stream.Info,
      Content = buffer.ToArray()
    };
  }

  public async Task<ObjectReadStream?> OpenReadAsync(string key,
    CancellationToken ct) {
    try {
      GetObjectResponse response = await client_.GetObjectAsync(
                                     new GetObjectRequest {
                                       BucketName = options_.Bucket,
                                       Key = ToObjectKey(key)
                                     },
                                     ct
                                   );
      ObjectInfo info = new() {
        Key = key,
        Size = response.ContentLength,
        ETag = response.ETag,
        LastModified = ToOffset(response.LastModified),
        ContentType = response.Headers.ContentType,
        Metadata = ReadMetadata(response.Metadata)
      };
      return new ObjectReadStream(info, response.ResponseStream, response);
    } catch (AmazonS3Exception ex) when (IsNotFound(ex)) {
      return null;
    }
  }

  public async Task<ObjectInfo?> HeadAsync(string key, CancellationToken ct) {
    try {
      GetObjectMetadataResponse response = await client_.GetObjectMetadataAsync(
                                             new GetObjectMetadataRequest {
                                               BucketName = options_.Bucket,
                                               Key = ToObjectKey(key)
                                             },
                                             ct
                                           );
      return new ObjectInfo {
        Key = key,
        Size = response.ContentLength,
        ETag = response.ETag,
        LastModified = ToOffset(response.LastModified),
        ContentType = response.Headers.ContentType,
        Metadata = ReadMetadata(response.Metadata)
      };
    } catch (AmazonS3Exception ex) when (IsNotFound(ex)) {
      return null;
    }
  }

  public async Task<string?> PutAsync(string key, ReadOnlyMemory<byte> content,
                                      PutOptions options,
                                      CancellationToken ct) {
    using MemoryStream stream = MemoryMarshal.TryGetArray(
                                  content,
                                  out ArraySegment<byte> segment
                                )
                                  ? new MemoryStream(
                                    segment.Array!,
                                    segment.Offset,
                                    segment.Count,
                                    false
                                  )
                                  : new MemoryStream(content.ToArray(), false);
    PutObjectRequest request = new() {
      BucketName = options_.Bucket,
      Key = ToObjectKey(key),
      InputStream = stream,
      AutoCloseStream = false,
      ContentType = options.ContentType,
      UseChunkEncoding = options_.UseChunkEncoding
    };
    if (options_.ConditionalWrites) {
      if (options.IfNoneMatch) {
        request.IfNoneMatch = "*";
      } else if (options.IfMatch != null) {
        request.IfMatch = options.IfMatch;
      }
    }

    AddMetadata(request.Metadata, options.Metadata);
    try {
      PutObjectResponse response = await client_.PutObjectAsync(request, ct);
      return response.ETag;
    } catch (AmazonS3Exception ex) when (
      ex.StatusCode is HttpStatusCode.PreconditionFailed
        or HttpStatusCode.Conflict) {
      // 409 is what S3 answers when two conditional writes race.
      throw new PreconditionFailedException(key, ex);
    }
  }

  public async Task PutFileAsync(string key, string path, string content_type,
                                 IReadOnlyDictionary<string, string>? metadata,
                                 CancellationToken ct) {
    TransferUtilityUploadRequest request = new() {
      BucketName = options_.Bucket,
      Key = ToObjectKey(key),
      FilePath = path,
      ContentType = content_type,
      PartSize = C_PART_SIZE_
    };
    AddMetadata(request.Metadata, metadata);
    using TransferUtility transfer = new(
      client_,
      new TransferUtilityConfig {
        MinSizeBeforePartUpload = 4 * C_PART_SIZE_
      }
    );
    await transfer.UploadAsync(request, ct);
  }

  public async Task DeleteAsync(string key, CancellationToken ct) {
    await client_.DeleteObjectAsync(
      new DeleteObjectRequest {
        BucketName = options_.Bucket,
        Key = ToObjectKey(key)
      },
      ct
    );
  }

  public async Task DeleteManyAsync(IReadOnlyCollection<string> keys,
                                    CancellationToken ct) {
    foreach (string[] batch in keys.Chunk(C_DELETE_BATCH_)) {
      try {
        await client_.DeleteObjectsAsync(
          new DeleteObjectsRequest {
            BucketName = options_.Bucket,
            Quiet = true,
            Objects = batch.Select(
                             k => new KeyVersion {
                               Key = ToObjectKey(k)
                             }
                           )
                           .ToList()
          },
          ct
        );
      } catch (AmazonS3Exception ex) when (
        ex.StatusCode == HttpStatusCode.NotImplemented) {
        // Stores without multi-object delete.
        foreach (string key in batch) {
          await DeleteAsync(key, ct);
        }
      }
    }
  }

  public async IAsyncEnumerable<ObjectInfo> ListAsync(string prefix,
    string? start_after,
    [EnumeratorCancellation] CancellationToken ct) {
    ListObjectsV2Request request = new() {
      BucketName = options_.Bucket,
      Prefix = prefix_ + prefix,
      StartAfter = start_after == null ? null : prefix_ + start_after
    };
    while (true) {
      ListObjectsV2Response response = await client_.ListObjectsV2Async(
                                         request,
                                         ct
                                       );
      foreach (S3Object item in response.S3Objects ?? []) {
        if (!item.Key.StartsWith(prefix_, StringComparison.Ordinal)) {
          continue;
        }

        yield return new ObjectInfo {
          Key = item.Key[prefix_.Length..],
          Size = item.Size ?? 0,
          ETag = item.ETag,
          LastModified = ToOffset(item.LastModified)
        };
      }

      if (response.IsTruncated != true ||
          string.IsNullOrEmpty(response.NextContinuationToken)) {
        yield break;
      }

      request.ContinuationToken = response.NextContinuationToken;
      request.StartAfter = null;
    }
  }

  private string ToObjectKey(string key) {
    if (key.Length == 0 ||
        key.StartsWith('/') ||
        key.Contains("..", StringComparison.Ordinal) ||
        key.Contains('\\')) {
      throw new ArgumentException($"Invalid object key '{key}'", nameof(key));
    }

    return prefix_ + key;
  }

  private static bool IsNotFound(AmazonS3Exception ex) {
    return ex.StatusCode == HttpStatusCode.NotFound;
  }

  private static DateTimeOffset ToOffset(DateTime? value) {
    return value == null
             ? DateTimeOffset.UnixEpoch
             : new DateTimeOffset(value.Value.ToUniversalTime(), TimeSpan.Zero);
  }

  private static void AddMetadata(MetadataCollection target,
                                  IReadOnlyDictionary<string, string>? source) {
    if (source == null) {
      return;
    }

    foreach (KeyValuePair<string, string> pair in source) {
      target.Add(pair.Key, pair.Value);
    }
  }

  private static Dictionary<string, string> ReadMetadata(
    MetadataCollection metadata) {
    Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
    foreach (string key in metadata.Keys) {
      // The SDK reports keys with their "x-amz-meta-" prefix.
      string name = key.StartsWith(
                      C_META_PREFIX_,
                      StringComparison.OrdinalIgnoreCase
                    )
                      ? key[C_META_PREFIX_.Length..]
                      : key;
      result[name] = metadata[key];
    }

    return result;
  }
}