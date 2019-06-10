﻿using Storage.Net.Blobs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amazon;
using Amazon.S3;
using Amazon.Runtime;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using System.Threading.Tasks;
using System.Threading;
using Storage.Net.Amazon.Aws.Blob;
using Storage.Net.Streaming;

namespace Storage.Net.Aws.Blobs
{
   /// <summary>
   /// Amazon S3 storage adapter for blobs
   /// </summary>
   class AwsS3BlobStorageProvider : IBlobStorage, IAwsS3BlobStorage
   {
      private readonly string _bucketName;
      private readonly AmazonS3Client _client;
      private readonly TransferUtility _fileTransferUtility;
      private bool _initialised;

      /// <summary>
      /// Returns reference to the native AWS S3 blob client.
      /// </summary>
      public IAmazonS3 NativeBlobClient => _client;

      //https://github.com/awslabs/aws-sdk-net-samples/blob/master/ConsoleSamples/AmazonS3Sample/AmazonS3Sample/S3Sample.cs

      /// <summary>
      /// Creates a new instance of <see cref="AwsS3BlobStorageProvider"/> for a given region endpoint/>
      /// </summary>
      public AwsS3BlobStorageProvider(string accessKeyId, string secretAccessKey, string bucketName, RegionEndpoint regionEndpoint)
         : this(accessKeyId, secretAccessKey, bucketName, new AmazonS3Config { RegionEndpoint = regionEndpoint ?? RegionEndpoint.EUWest1 })
      {
      }

      /// <summary>
      /// Creates a new instance of <see cref="AwsS3BlobStorageProvider"/> for an S3-compatible storage provider hosted on an alternative service URL/>
      /// </summary>
      public AwsS3BlobStorageProvider(string accessKeyId, string secretAccessKey, string bucketName, string serviceUrl)
         : this(accessKeyId, secretAccessKey, bucketName, new AmazonS3Config
         {
            RegionEndpoint = RegionEndpoint.USEast1,
            ServiceURL = serviceUrl
         })
      {
      }

      /// <summary>
      /// Creates a new instance of <see cref="AwsS3BlobStorageProvider"/> for a given S3 client configuration/>
      /// </summary>
      public AwsS3BlobStorageProvider(string accessKeyId, string secretAccessKey, string bucketName, AmazonS3Config clientConfig)
      {
         if (accessKeyId == null) throw new ArgumentNullException(nameof(accessKeyId));
         if (secretAccessKey == null) throw new ArgumentNullException(nameof(secretAccessKey));
         _bucketName = bucketName ?? throw new ArgumentNullException(nameof(bucketName));

         _client = new AmazonS3Client(new BasicAWSCredentials(accessKeyId, secretAccessKey), clientConfig);
         _fileTransferUtility = new TransferUtility(_client);
      }

      private async Task<AmazonS3Client> GetClientAsync()
      {
         if (!_initialised)
         {
            try
            {
               var request = new PutBucketRequest { BucketName = _bucketName };

               await _client.PutBucketAsync(request);

               _initialised = true;
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode == "BucketAlreadyOwnedByYou")
            {
               //ignore this error as bucket already exists
            }
         }

         return _client;
      }

      /// <summary>
      /// Lists all buckets, optionaly filtering by prefix. Prefix filtering happens on client side.
      /// </summary>
      public async Task<IReadOnlyCollection<Blob>> ListAsync(ListOptions options = null, CancellationToken cancellationToken = default)
      {
         if (options == null) options = new ListOptions();

         GenericValidation.CheckBlobPrefix(options.FilePrefix);

         var request = new ListObjectsV2Request()
         {
            BucketName = _bucketName,
            Prefix = options.FilePrefix ?? null
         };
         if (options.MaxResults.HasValue) request.MaxKeys = options.MaxResults.Value;


         //todo: paging
         AmazonS3Client client = await GetClientAsync();
         ListObjectsV2Response response = await client.ListObjectsV2Async(request, cancellationToken);

         return response.S3Objects
            .Select(s3Obj => new Blob(StoragePath.RootFolderPath, s3Obj.Key, BlobItemKind.File))
            .Where(options.IsMatch)
            .Where(bid => (options.FolderPath == null || bid.FolderPath == options.FolderPath))
            .Where(bid => options.BrowseFilter == null || options.BrowseFilter(bid))
            .ToList();
      }

      public async Task WriteAsync(Blob blob, Stream sourceStream, bool append = false, CancellationToken cancellationToken = default)
      {
         if (append) throw new NotSupportedException();

         GenericValidation.CheckBlobFullPath(blob);
         GenericValidation.CheckSourceStream(sourceStream);

         //http://docs.aws.amazon.com/AmazonS3/latest/dev/HLuploadFileDotNet.html

         string fullPath = StoragePath.Normalize(blob, false);
         await _fileTransferUtility.UploadAsync(sourceStream, _bucketName, fullPath, cancellationToken);
      }

      /// <summary>
      /// S3 doesnt support this natively and will cache everything in MemoryStream until disposed.
      /// </summary>
      public Task<Stream> OpenWriteAsync(Blob blob, bool append = false, CancellationToken cancellationToken = default)
      {
         if (append) throw new NotSupportedException();
         GenericValidation.CheckBlobFullPath(blob);
         string fullPath = StoragePath.Normalize(blob, false);

         var callbackStream = new FixedStream(new MemoryStream(), null, fx =>
         {
            var ms = (MemoryStream)fx.Parent;
            ms.Position = 0;

            _fileTransferUtility.UploadAsync(ms, _bucketName, fullPath, cancellationToken).Wait();
         });

         return Task.FromResult<Stream>(callbackStream);
      }

      public async Task<Stream> OpenReadAsync(string fullPath, CancellationToken cancellationToken = default)
      {
         GenericValidation.CheckBlobFullPath(fullPath);

         fullPath = StoragePath.Normalize(fullPath, false);
         GetObjectResponse response = await GetObjectAsync(fullPath);
         if (response == null) return null;

         return new FixedStream(response.ResponseStream, length: response.ContentLength);
      }

      public Task DeleteAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
      {
         return Task.WhenAll(fullPaths.Select(fullPath => DeleteAsync(fullPath, cancellationToken)));
      }

      private async Task DeleteAsync(string fullPath, CancellationToken cancellationToken = default)
      {
         GenericValidation.CheckBlobFullPath(fullPath);

         fullPath = StoragePath.Normalize(fullPath, false);
         AmazonS3Client client = await GetClientAsync();
         await client.DeleteObjectAsync(_bucketName, fullPath, cancellationToken);
      }

      public async Task<IReadOnlyCollection<bool>> ExistsAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
      {
         return await Task.WhenAll(fullPaths.Select(ExistsAsync));
      }

      private async Task<bool> ExistsAsync(string fullPath)
      {
         GenericValidation.CheckBlobFullPath(fullPath);

         try
         {
            fullPath = StoragePath.Normalize(fullPath, false);
            using (GetObjectResponse response = await GetObjectAsync(fullPath))
            {
               if (response == null) return false;
            }
         }
         catch (StorageException ex)
         {
            if (ex.ErrorCode == ErrorCode.NotFound) return false;
         }

         return true;
      }

      public async Task<IReadOnlyCollection<Blob>> GetBlobsAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
      {
         return await Task.WhenAll(fullPaths.Select(GetBlobAsync));
      }

      private async Task<Blob> GetBlobAsync(string fullPath)
      {
         GenericValidation.CheckBlobFullPath(fullPath);

         try
         {
            fullPath = StoragePath.Normalize(fullPath, false);
            using (GetObjectResponse obj = await GetObjectAsync(fullPath))
            {
               //ETag contains actual MD5 hash, not sure why!

               if(obj != null)
               {
                  var r = new Blob(fullPath);
                  r.MD5 = obj.ETag.Trim('\"');
                  r.Size = obj.ContentLength;
                  r.LastModificationTime = obj.LastModified.ToUniversalTime();
                  return r;
               }
            }
         }
         catch (StorageException ex) when (ex.ErrorCode == ErrorCode.NotFound)
         {
            //if blob is not found, don't return any information
         }

         return null;
      }

      private async Task<GetObjectResponse> GetObjectAsync(string key)
      {
         var request = new GetObjectRequest { BucketName = _bucketName, Key = key };
         AmazonS3Client client = await GetClientAsync();

         try
         {
            GetObjectResponse response = await client.GetObjectAsync(request);
            return response;
         }
         catch (AmazonS3Exception ex)
         {
            if (IsDoesntExist(ex)) return null;

            TryHandleException(ex);
            throw;
         }
      }


      private static bool TryHandleException(AmazonS3Exception ex)
      {
         if (IsDoesntExist(ex))
         {
            throw new StorageException(ErrorCode.NotFound, ex);
         }

         return false;
      }

      private static bool IsDoesntExist(AmazonS3Exception ex)
      {
         return ex.ErrorCode == "NoSuchKey";
      }

      public void Dispose()
      {
      }

      public Task<ITransaction> OpenTransactionAsync()
      {
         return Task.FromResult(EmptyTransaction.Instance);
      }

      /// <summary>
      /// Get presigned url for upload object to Blob Storage.
      /// </summary>
      public async Task<string> GetUploadUrlAsync(string fullPath, string mimeType, int expiresInSeconds = 86000)
      {
         return await GetPresignedUrlAsync(fullPath, mimeType, expiresInSeconds, HttpVerb.PUT);
      }

      /// <summary>
      /// Get presigned url for download object from Blob Storage.
      /// </summary>
      public async Task<string> GetDownloadUrlAsync(string fullPath, string mimeType, int expiresInSeconds = 86000)
      {
         return await GetPresignedUrlAsync(fullPath, mimeType, expiresInSeconds, HttpVerb.GET);
      }

      /// <summary>
      /// Get presigned url for requested operation with Blob Storage.
      /// </summary>
      public async Task<string> GetPresignedUrlAsync(string fullPath, string mimeType, int expiresInSeconds, HttpVerb verb)
      {
         IAmazonS3 client = await GetClientAsync();

         return client.GetPreSignedURL(new GetPreSignedUrlRequest()
         {
            BucketName = _bucketName,
            ContentType = mimeType,
            Expires = DateTime.UtcNow.AddSeconds(expiresInSeconds),
            Key = StoragePath.Normalize(fullPath, false),
            Verb = verb,
         });
      }
   }
}
