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

		/// <summary>
		/// Downloads a GET response to <paramref name="filePath"/> and refuses a body larger than
		/// <paramref name="maxBytes"/>.
		/// </summary>
		/// <remarks>
		/// <para>
		/// Unlike <see cref="DownloadFileByGetAsync"/>, EVERY status streams through the same byte-counting
		/// copy loop: an error body is written to the file too, so a caller reads the status from the response
		/// and the server's actual message from the file. The unbounded overload buffers a final non-success
		/// body into memory and writes no file, which leaves a caller with nothing to read and no ceiling.
		/// </para>
		/// <para>
		/// The ceiling is enforced before each write, so the file never exceeds it and at most one buffer past
		/// it is read off the socket; a crossing throws <see cref="CreatioResponseTooLargeException"/> and the
		/// partial file is removed. Because retrying an oversized body cannot succeed, the ceiling is not
		/// subject to the client's retry policy.
		/// </para>
		/// </remarks>
		/// <param name="url">Absolute or application-relative URL to GET.</param>
		/// <param name="filePath">Destination path; overwritten.</param>
		/// <param name="maxBytes">Maximum body bytes to accept. Zero or greater.</param>
		/// <param name="requestTimeout">Deadline in milliseconds across send and every body read.</param>
		/// <param name="cancellationToken">Cancels the transfer.</param>
		/// <exception cref="CreatioResponseTooLargeException">The body reached <paramref name="maxBytes"/>.</exception>
		/// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is negative.</exception>
		Task<HttpResponseMessage> DownloadFileByGetBoundedAsync(string url, string filePath, long maxBytes,
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
