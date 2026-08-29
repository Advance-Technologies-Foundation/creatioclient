using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Creatio.Client.Dto;

namespace Creatio.Client
{
	public interface ICreatioClient
	{

		#region Events: Public

		event EventHandler<WsMessage> MessageReceived;

		event EventHandler<WebSocketState> ConnectionStateChanged;

		#endregion

		#region Methods: Public

		/// <summary>
		/// Calls a configuration service in the Creatio application.
		/// </summary>
		/// <param name="serviceName">The name of the service to call.</param>
		/// <param name="serviceMethod">The method of the service to call.</param>
		/// <param name="requestData">The data to send with the request.</param>
		/// <param name="requestTimeout">Optional. The timeout for the request in milliseconds. Default is 100000.</param>
		/// <returns>The response from the service as a string.</returns>
		string CallConfigurationService(string serviceName, string serviceMethod, string requestData,
			int requestTimeout = 100_000);

		/// <summary>
		/// Downloads a file from the specified URL and saves it to the provided file path.
		/// </summary>
		/// <param name="url">The URL of the file to download.</param>
		/// <param name="filePath">The path where the downloaded file should be saved.</param>
		/// <param name="requestData">The data to send with the request.</param>
		/// <param name="requestTimeout">Optional. The timeout for the request in milliseconds. Default is 100000.</param>
		void DownloadFile(string url, string filePath, string requestData, int requestTimeout = 100_000);

		/// <summary>
		/// Downloads a file via an HTTP GET request to the specified URL and saves it to disk,
		/// preserving binary content. Use this for GET-only file endpoints (e.g. OData media links
		/// or [WebGet] services) where <see cref="DownloadFile"/> (which issues a POST) is unsuitable.
		/// </summary>
		/// <param name="url">The absolute URL of the file to download.</param>
		/// <param name="filePath">The path where the downloaded file should be saved.</param>
		/// <param name="requestTimeout">Optional. The timeout for the request in milliseconds. Default is 100000.</param>
		void DownloadFileByGet(string url, string filePath, int requestTimeout = 100_000);

		/// <summary>
		/// Executes a GET request to the specified URL.
		/// </summary>
		/// <param name="url">The URL to send the GET request to.</param>
		/// <param name="requestTimeout">Optional. The timeout for the request in milliseconds. Default is 100000.</param>
		/// <param name="maxAttempts">Optional. Total number of attempts to make (minimum 1; values below 1 are treated as 1). Default is 1 (a single attempt, no retries).</param>
		/// <param name="delaySec">Optional. The delay in seconds before retrying the request. Default is 1.</param>
		/// <returns>The response from the GET request as a string.</returns>
		string ExecuteGetRequest(string url, int requestTimeout = 100_000, int maxAttempts = 1, int delaySec = 1);

		
		/// <summary>
		/// Executes a POST request to the specified URL.
		/// </summary>
		/// <param name="url">The URL to send the POST request to.</param>
		/// <param name="requestData">The data to send with the request.</param>
		/// <param name="requestTimeout">Optional. The timeout for the request in milliseconds. Default is 100000.</param>
		/// <param name="maxAttempts">Optional. Total number of attempts to make (minimum 1; values below 1 are treated as 1). Default is 1 (a single attempt, no retries).</param>
		/// <param name="delaySec">Optional. The delay in seconds before retrying the request. Default is 1.</param>
		/// <returns>The response from the POST request as a string.</returns>
		string ExecutePostRequest(string url, string requestData, int requestTimeout = 100_000, int maxAttempts = 1, int delaySec = 1);

		/// <summary>
		/// Executes a PATCH request to the specified URL. Used for OData v4 partial updates,
		/// where only the supplied fields are changed on the addressed record.
		/// </summary>
		/// <param name="url">The URL to send the PATCH request to (typically an OData entity addressed by key, e.g. <c>odata/Contact(&lt;guid&gt;)</c>).</param>
		/// <param name="requestData">The JSON body containing the field/value pairs to change.</param>
		/// <param name="requestTimeout">Optional. The timeout for the request in milliseconds. Default is 100000.</param>
		/// <param name="maxAttempts">Optional. Total number of attempts to make (minimum 1; values below 1 are treated as 1). Default is 1 (a single attempt, no retries).</param>
		/// <param name="delaySec">Optional. The delay in seconds before retrying the request. Default is 1.</param>
		/// <returns>The response from the PATCH request as a string (empty for the typical 204 No Content).</returns>
		string ExecutePatchRequest(string url, string requestData, int requestTimeout = 100_000, int maxAttempts = 1, int delaySec = 1);

		/// <summary>
		/// Executes a PUT request to the specified URL.
		/// </summary>
		/// <param name="url">The URL to send the PUT request to.</param>
		/// <param name="requestData">The JSON data to send with the request.</param>
		/// <param name="requestTimeout">Optional. The timeout for the request in milliseconds. Default is 100000.</param>
		/// <param name="maxAttempts">Optional. Total number of attempts to make (minimum 1; values below 1 are treated as 1). Default is 1 (a single attempt, no retries).</param>
		/// <param name="delaySec">Optional. The delay in seconds before retrying the request. Default is 1.</param>
		/// <returns>The response from the PUT request as a string.</returns>
		string ExecutePutRequest(string url, string requestData, int requestTimeout = 100_000, int maxAttempts = 1, int delaySec = 1);

		/// <summary>
		/// Logs in to the Creatio application.
		/// </summary>
		void Login();
		
		/// <summary>
		/// Uploads a file to the Application Lifecycle Management (ALM) system.
		/// </summary>
		/// <param name="url">The URL of the ALM system to upload the file to.</param>
		/// <param name="filePath">The path of the file to be uploaded.</param>
		/// <returns>The response from the ALM system as a string.</returns>
		string UploadAlmFile(string url, string filePath);

		/// <summary>
		/// Uploads a file to the specified URL.
		/// </summary>
		/// <param name="url">The URL to upload the file to.</param>
		/// <param name="filePath">The path of the file to be uploaded.</param>
		/// <param name="requestTimeout">Optional. The timeout for the request in milliseconds. Default is 100000.</param>
		/// <param name="chunkSize">Upload chunk size (Default 1Mb)</param>
		/// <returns>The response from the server as a string.</returns>
		string UploadFile(string url, string filePath, int requestTimeout = 100_000, int chunkSize = 1 * 1024 * 1024);
		
		/// <inheritdoc cref="UploadFile"/>
		Task<string> UploadFileAsync(string url, string filePath, int defaultTimeout = 100_000, int chunkSize = 1 * 1024 * 1024);

		/// <summary>
		/// Starts listening for incoming messages from the Creatio application.
		/// </summary>
		/// <param name="cancellationToken">A CancellationToken to observe while waiting for the task to complete.</param>
		void StartListening(CancellationToken cancellationToken);

		/// <summary>
		/// Sets a custom retry policy for HTTP calls.
		/// </summary>
		/// <param name="maxAttempts">Total number of attempts to make for the HTTP call (minimum 1; values below 1 are treated as 1).</param>
		/// <param name="delaySec">The delay in seconds before retrying the HTTP call.</param>
		/// <param name="retryPolicy">The retry policy to use for the HTTP call. See <see cref="RetryPolicy"/>.</param>
		void SetRetryPolicy(int maxAttempts, int delaySec, RetryPolicy retryPolicy);

		/// <summary>
		/// Uploads an attachment to a Creatio entity.
		/// </summary>
		/// <param name="uploadInfo">Information about the file to upload</param>
		/// <param name="timeout">Timeout for the upload request in milliseconds. Default is 100000.</param>
		/// <param name="chunkSize">Size of each chunk to upload in bytes. Default is 1 MB.</param>
		/// <returns>Response information from Creatio after file uploaded</returns>
		Task<string> UploadAttachmentAsync(FileUploadInfo uploadInfo, int timeout = 100_000,
			int chunkSize = 1 * 1024 * 1024);


		/// <summary>
		/// Downloads an attachment from a Creatio entity and saves it to the specified file path.
		/// </summary>
		/// <param name="schemaName">The name of the entity schema containing the attachment.</param>
		/// <param name="recordId">The unique identifier of the record containing the attachment.</param>
		/// <param name="filePath">The local file path where the attachment will be saved.</param>
		/// <param name="timeout">Timeout for the download request in milliseconds. Default is 100000.</param>
		/// <returns>True if the download was successful; otherwise, false.</returns>
		bool DownloadAttachment(string schemaName, Guid recordId, string filePath, int timeout = 100_000);

		#endregion

	}
}
