using Microsoft.AspNetCore.Mvc;
using Minio;
using Minio.ApiEndpoints;
using Minio.DataModel.Args;
using System.Reactive.Linq;
using System.Text;

namespace MinioWebExample.Controllers;

[ApiController]
[Route("[controller]")]
public class S3Controller : ControllerBase
{
    private readonly IMinioClient _minioClient;

    public S3Controller(IMinioClient minioClient)
    {
        _minioClient = minioClient;
    }

    [HttpGet("{bucketId}/{file}")]
    [ProducesResponseType(typeof(string[]), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUrl(string bucketId, string file)
    {
        var args = new PresignedGetObjectArgs().WithBucket(bucketId).WithObject(file).WithExpiry(60 * 60 * 24);

        return Ok(await _minioClient.PresignedGetObjectAsync(args));
    }

    [HttpGet]
    [ProducesResponseType(typeof(string[]), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetListBuckets()
    {
        var sb = new StringBuilder();

        var listBuckets = await _minioClient.ListBucketsAsync();

        foreach (var bucket in listBuckets.Buckets)
        {
            sb.AppendLine($"{bucket.Name} {bucket.CreationDateDateTime}");
        }

        return Ok(sb.ToString());
    }

    [HttpPost]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create(IFormFile file, [FromQuery] string? bucketName = null, [FromQuery] string? customFileName = null)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("Файл не был загружен.");
        }

        bucketName ??= "academy-bucket";

        customFileName ??= $"uploaded-file-{Guid.NewGuid()}";

        return await UploadFile(file, bucketName, customFileName);
    }

    [HttpGet("file/{bucketName}/{fileName}")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFile(string bucketName, string fileName)
    {
        try
        {
            var objstatreply = await _minioClient.StatObjectAsync(new StatObjectArgs().WithBucket(bucketName).WithObject(fileName));

            if (objstatreply == null || objstatreply.DeleteMarker)
            {
                return NotFound($"Файл {fileName} не найден в бакете с идентификатором {bucketName}");
            }

            using var memoryStream = new MemoryStream();

            await _minioClient.GetObjectAsync(new GetObjectArgs().WithBucket(bucketName).WithObject(fileName)
                .WithCallbackStream((stream) =>
                {
                    stream.CopyToAsync(memoryStream);
                }));

            var byteArray = memoryStream.ToArray();
            return File(byteArray, "application/octet-stream", fileName);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Ошибка при чтении файла: {ex.Message}");
        }
    }

    [HttpGet("{bucketName}/files")]
    [ProducesResponseType(typeof(string[]), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFilesList(string bucketName)
    {
        try
        {
            var objectList = new List<string>();
            var args = new ListObjectsArgs().WithBucket(bucketName);

            var observable = _minioClient.ListObjectsAsync(args);

            var fileNames = await observable.Select(obj => obj.Key).ToList();

            return Ok(fileNames);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Ошибка при чтении списка файлов: {ex.Message}");
        }
    }

    [HttpPut("{bucketName}/{fileName}")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateFile(string bucketName, string fileName, IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("Файл не был загружен.");
        }

        try
        {
            return await UploadFile(file, bucketName, fileName);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Ошибка при обновлении файла: {ex.Message}");
        }
    }


    [HttpPost]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    private async Task<IActionResult> UploadFile(IFormFile file, string bucketName, string objectName, string contentType = "application/octet-stream")
    {
        try
        {
            var args = new BucketExistsArgs().WithBucket(bucketName);
            var found = await _minioClient.BucketExistsAsync(args);

            if (!found)
            {
                await _minioClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucketName));
            }

            using (var stream = file.OpenReadStream())
            {
                var putArgs = new PutObjectArgs()
                    .WithBucket(bucketName)
                    .WithObject(objectName)
                    .WithStreamData(stream)
                    .WithObjectSize(file.Length)
                    .WithContentType(contentType);

                await _minioClient.PutObjectAsync(putArgs);
            }

            return Ok(objectName);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Ошибка при загрузке файла: {ex.Message}");
        }
    }

    [HttpDelete("{bucketName}/{fileName}")]
    public async Task<IActionResult> DeleteFile(string bucketName, string fileName)
    {
        try
        {
            var args = new StatObjectArgs().WithBucket(bucketName).WithObject(fileName);

            await _minioClient.StatObjectAsync(args);

            var deleteArgs = new RemoveObjectArgs().WithBucket(bucketName).WithObject(fileName);

            await _minioClient.RemoveObjectAsync(deleteArgs);

            return NoContent();
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Ошибка при удалении файла: {ex.Message}");
        }
    }

    [HttpDelete("{bucketName}")]
    public async Task<IActionResult> DeleteBucket(string bucketName, int packSizeForRemoval)
    {
        try
        {
            var observableObjects = _minioClient.ListObjectsAsync(new ListObjectsArgs().WithBucket(bucketName));
            var objectKeys = new List<string>();

            await observableObjects
                .ForEachAsync(async obj =>
                {
                    objectKeys.Add(obj.Key);
                    if (objectKeys.Count > packSizeForRemoval)
                    {
                        await DeleteObjectsInBatch(bucketName, objectKeys);
                        objectKeys.Clear();
                    }
                });

            if (objectKeys.Count > 0)
            {
                await DeleteObjectsInBatch(bucketName, objectKeys);
            }

            var deleteArgs = new RemoveBucketArgs().WithBucket(bucketName);
            await _minioClient.RemoveBucketAsync(deleteArgs);

            return NoContent();
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Ошибка при удалении бакета: {ex.Message}");
        }
    }

    private async Task DeleteObjectsInBatch(string bucketName, List<string> objectKeys)
    {
        try
        {
            var removeObjectsArgs = new RemoveObjectsArgs().WithBucket(bucketName).WithObjects(objectKeys);
            await _minioClient.RemoveObjectsAsync(removeObjectsArgs);
        }
        catch (Exception ex)
        {
            Console.WriteLine(StatusCode(500, $"Ошибка при удалении объектов: {ex.Message}"));
        }
    }
}