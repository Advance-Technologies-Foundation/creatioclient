using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Creatio.Client.Dto;

namespace Creatio.Client
{
	/// <summary>
	/// Cancellation-aware HTTP operations for <see cref="ICreatioClient"/> consumers.
	/// Every method transfers ownership of the returned response to the caller, which must dispose it.
	/// Download methods stream content to the requested file before returning; their response content stream
	/// is therefore positioned at its end, while status and response/content headers remain available.
	/// </summary>
	public interface IAsyncCreatioClient : ICreatioClient
	{
		Task<HttpResponseMessage> CallConfigurationServiceAsync(string serviceName, string serviceMethod,
			string requestData, int requestTimeout = 100_000,
			CancellationToken cancellationToken = default(CancellationToken));

		Task<HttpResponseMessage> DownloadFileAsync(string url, string filePath, string requestData,
			int requestTimeout = 100_000, CancellationToken cancellationToken = default(CancellationToken));

		Task<HttpResponseMessage> DownloadFileByGetAsync(string url, string filePath,
			int requestTimeout = 100_000, CancellationToken cancellationToken = default(CancellationToken));

		Task<HttpResponseMessage> ExecuteGetRequestAsync(string url, int requestTimeout = 100_000,
			int maxAttempts = 1, int delaySec = 1,
			CancellationToken cancellationToken = default(CancellationToken));

		Task<HttpResponseMessage> ExecutePostRequestAsync(string url, string requestData,
			int requestTimeout = 100_000, int maxAttempts = 1, int delaySec = 1,
			CancellationToken cancellationToken = default(CancellationToken));

		Task<HttpResponseMessage> ExecutePatchRequestAsync(string url, string requestData,
			int requestTimeout = 100_000, int maxAttempts = 1, int delaySec = 1,
			CancellationToken cancellationToken = default(CancellationToken));

		Task<HttpResponseMessage> ExecutePutRequestAsync(string url, string requestData,
			int requestTimeout = 100_000, int maxAttempts = 1, int delaySec = 1,
			CancellationToken cancellationToken = default(CancellationToken));

		Task<HttpResponseMessage> LoginAsync(int requestTimeout = 100_000,
			CancellationToken cancellationToken = default(CancellationToken));

		Task<HttpResponseMessage> ExecuteDeleteRequestAsync(string url, string requestData,
			int requestTimeout = 10_000, int maxAttempts = 1, int delaySec = 1,
			CancellationToken cancellationToken = default(CancellationToken));

		Task<HttpResponseMessage> UploadAlmFileAsync(string url, string filePath,
			int requestTimeout = 100_000, CancellationToken cancellationToken = default(CancellationToken));

		Task<HttpResponseMessage> UploadAlmFileByChunkAsync(string url, string filePath,
			int requestTimeout = 100_000, CancellationToken cancellationToken = default(CancellationToken));

		Task<HttpResponseMessage> UploadChunkAlmFileAsync(string url, byte[] data, int downloadedSize,
			int totalSize, int requestTimeout = 100_000,
			CancellationToken cancellationToken = default(CancellationToken));

		Task<HttpResponseMessage> UploadFileAsync(string url, string filePath, int defaultTimeout,
			int chunkSize, CancellationToken cancellationToken);

		Task<HttpResponseMessage> UploadStaticFileAsync(string url, string filePath, string folderName,
			int defaultTimeout, int chunkSize, CancellationToken cancellationToken);

		Task<HttpResponseMessage> UploadFile_originalAsync(string url, string filePath,
			int defaultTimeout = 100_000,
			CancellationToken cancellationToken = default(CancellationToken));

		Task<HttpResponseMessage> UploadAttachmentResponseAsync(FileUploadInfo uploadInfo,
			int timeout = 100_000, int chunkSize = 1 * 1024 * 1024,
			CancellationToken cancellationToken = default(CancellationToken));

		Task<HttpResponseMessage> DownloadAttachmentAsync(string schemaName, Guid recordId, string filePath,
			int timeout = 100_000, CancellationToken cancellationToken = default(CancellationToken));
	}
}
