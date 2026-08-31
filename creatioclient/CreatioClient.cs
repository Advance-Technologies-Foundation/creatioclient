using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Creatio.Client.Dto;
using Newtonsoft.Json;

namespace Creatio.Client
{

	#region Class: CreatioClient

	public class CreatioClient : IAsyncCreatioClient, IDisposable
	{

		#region Constants: Private

		private const string WorkspaceId = "0";

		#endregion

		#region Fields: Private

		private readonly string _userName;
		private readonly string _userPassword;
		private readonly bool _isNetCore;
		private readonly bool _useUntrustedSsl = true;
		private readonly CookieContainer _authCookie = new CookieContainer();
		private string _oauthToken;
		private int _maxAttempts = 1;
		private int _delaySec = 1;
		private RetryPolicy _retryPolicy = RetryPolicy.Simple;
		private readonly ICredentials _credentials;
		private string _appUrl;
		private readonly object _httpClientLock = new object();
		private volatile HttpClient _httpClient;
		private volatile CreatioAuthenticationHandler _authenticationHandler;
		private bool _disposed;

		#endregion

		#region Properties: Private

		private string AppUrl {
			get => _appUrl;
			set => _appUrl = NormalizeUrl(value);
		}

		#endregion

		#region Properties: Internal

		internal CookieContainer AuthCookie {
			get {
				EnsureLegacyAuthentication();
				return _authCookie;
			}
		}

		[SuppressMessage("Security", "S4830:Server certificates should be verified during SSL/TLS connections",
			Justification = "The public useUntrustedSsl option intentionally supports self-signed on-premise Creatio instances.")]
		private HttpClient HttpClient {
			get {
				ThrowIfDisposed();
				if (_httpClient != null) {
					return _httpClient;
				}
				lock (_httpClientLock) {
					ThrowIfDisposed();
					if (_httpClient == null) {
						HttpClientHandler primaryHandler = new HttpClientHandler {
							AllowAutoRedirect = false,
							CookieContainer = _authCookie,
							UseCookies = true,
							Credentials = CreateScopedCredentials(_credentials,
								new Uri(AppUrl.TrimEnd('/') + "/"))
						};
						if (_useUntrustedSsl) {
							primaryHandler.ServerCertificateCustomValidationCallback =
								(request, certificate, chain, errors) => true; // NOSONAR: opt-in legacy support for self-signed on-premise Creatio.
						}
						_authenticationHandler = new CreatioAuthenticationHandler(
							new Uri(AppUrl), _authCookie, _userName, _userPassword, _credentials,
							() => _oauthToken, () => TimeZoneOffset, () => SkipPing, _isNetCore, primaryHandler);
						_httpClient = new HttpClient(_authenticationHandler) { Timeout = Timeout.InfiniteTimeSpan };
					}
					return _httpClient;
				}
			}
		}

		#endregion

		#region Properties: Public

		/// <summary>
		/// Gets or sets the login time zone offset, in the same UTC-minus-local minutes convention as
		/// JavaScript <c>Date.getTimezoneOffset()</c>. When unset, the current local offset is calculated
		/// when password login starts. OAuth client-credentials and NTLM authentication do not send this value.
		/// </summary>
		public int? TimeZoneOffset { get; set; }

		public bool SkipPing { get; set; }

		#endregion

		#region Events: Public

		public event EventHandler<WsMessage> MessageReceived;

		public event EventHandler<WebSocketState> ConnectionStateChanged;

		#endregion

		#region Methods: Private

		/// <summary>
		/// Ensures the URL has no trailing slash.
		/// </summary>
		/// <param name="url">The URL to process.</param>
		/// <returns>The URL without a trailing slash.</returns>
		private static string NormalizeUrl(string url) {
			return url.TrimEnd('/');
		}

		private static async Task<string> GetAccessTokenByClientCredentials(string authApp, string clientId,
			string clientSecret){
			using (HttpClient client = new HttpClient()) {
				Dictionary<string, string> body = new Dictionary<string, string> {
					{"client_id", clientId},
					{"client_secret", clientSecret},
					{"grant_type", "client_credentials"}
				};
				HttpContent httpContent = new FormUrlEncodedContent(body);
				HttpResponseMessage response = await client.PostAsync(authApp, httpContent).ConfigureAwait(false);
				string content = await response.Content.ReadAsStringAsync();
				TokenResponse token = JsonConvert.DeserializeObject<TokenResponse>(content);
				return token.AccessToken;
			}
		}

		private static void ValidateUploadInfo(FileUploadInfo uploadInfo) {
			if (uploadInfo == null) {
				throw new ArgumentNullException(nameof(uploadInfo), "UploadInfo cannot be null");
			}
			if (string.IsNullOrEmpty(uploadInfo.FilePath) || !File.Exists(uploadInfo.FilePath)) {
				throw new FileNotFoundException("FilePath is null or file does not exist", uploadInfo.FilePath);
			}
			if (string.IsNullOrEmpty(uploadInfo.EntitySchemaName)) {
				throw new ArgumentException("EntitySchemaName cannot be null or empty", nameof(uploadInfo.EntitySchemaName));
			}
			if (string.IsNullOrEmpty(uploadInfo.ColumnName)) {
				throw new ArgumentException("ColumnName cannot be null or empty", nameof(uploadInfo.ColumnName));
			}
			if (string.IsNullOrEmpty(uploadInfo.ParentColumnName)) {
				throw new ArgumentException("ParentColumnName cannot be null or empty", nameof(uploadInfo.ParentColumnName));
			}
			if (uploadInfo.ParentColumnValue == Guid.Empty) {
				throw new ArgumentException("ParentColumnValue cannot be empty Guid", nameof(uploadInfo.ParentColumnValue));
			}
		}

		private static Uri BuildUploadUri(string baseUrl, long totalFileLength, Guid fileId, FileUploadInfo uploadInfo,
				string fileName, string mime) {
			var sb = new StringBuilder();
			sb.Append(baseUrl);
			sb.Append($"?totalFileLength={totalFileLength}");
			sb.Append($"&fileId={fileId}");
			sb.Append($"&columnName={uploadInfo.ColumnName}");
			sb.Append($"&fileName={fileName}&mimeType={Uri.EscapeDataString(mime)}");
			sb.Append($"&parentColumnName={uploadInfo.ParentColumnName}");
			sb.Append($"&parentColumnValue={uploadInfo.ParentColumnValue}");
			sb.Append($"&entitySchemaName={uploadInfo.EntitySchemaName}");
			if (uploadInfo.AdditionalParams != null && uploadInfo.AdditionalParams.Count > 0) {
				var serializedParams = JsonConvert.SerializeObject(uploadInfo.AdditionalParams);
				sb.Append($"&AdditionalParams={Uri.EscapeDataString(serializedParams)}");
			}
			Uri.TryCreate(sb.ToString(), UriKind.Absolute, out Uri uri);
			return uri;
		}

		private static void HandleUploadResponse(HttpResponseMessage response, string resultString,
				long totalBytesRead, long totalLength) {
			try {
				FileUploadResponseDto dto = JsonConvert.DeserializeObject<FileUploadResponseDto>(resultString);
				if (response.StatusCode == HttpStatusCode.OK && dto.Success) {
					int percentageUploaded = (int)((totalBytesRead * 100) / totalLength);
					Console.WriteLine($"Chunk upload OK [{percentageUploaded} %]: {totalBytesRead} of {totalLength}");
				} else {
					Console.WriteLine($"Error: {dto.ErrorInfo?.ErrorCode} {dto.ErrorInfo?.Message}");
				}	
			} catch {
				Console.WriteLine("Error deserializing upload response: " + resultString);
				throw new ArgumentException("Error deserializing upload response", nameof(response));
			}
		}

		private string CreateConfigurationServiceUrl(string serviceName, string methodName){
			return $"{AppUrl}/{WorkspaceId}/rest/{serviceName}/{methodName}";
		}

		private async Task EnsureAuthenticatedAsync(int requestTimeout, CancellationToken cancellationToken)
		{
			_ = HttpClient;
			using (CancellationTokenSource timeout = CreateTimeout(requestTimeout, cancellationToken)) {
				await _authenticationHandler.EnsureAuthenticatedForRequestAsync(timeout.Token).ConfigureAwait(false);
			}
		}

		private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> requestFactory,
			int requestTimeout, int maxAttempts, int delaySeconds, CancellationToken cancellationToken,
			HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
			SessionRecoveryState sessionRecovery = null)
		{
			sessionRecovery = sessionRecovery ?? new SessionRecoveryState();
			if (!sessionRecovery.AuthenticationComplete) {
				await EnsureAuthenticatedAsync(100_000, cancellationToken).ConfigureAwait(false);
			}
			if (maxAttempts < 1) {
				maxAttempts = 1;
			}
			int multiplier = 1;
			int attempt = 1;
			while (attempt <= maxAttempts) {
				using (HttpRequestMessage request = requestFactory())
				using (CancellationTokenSource timeout = CreateTimeout(requestTimeout, cancellationToken)) {
					try {
						return await HttpClient.SendAsync(request, completionOption, timeout.Token)
							.ConfigureAwait(false);
					} catch (CreatioSessionExpiredException exception) {
						if (sessionRecovery.Attempted || cancellationToken.IsCancellationRequested) {
							return exception.Response;
						}
						using (exception.Response) {
							sessionRecovery.Attempted = true;
							await _authenticationHandler.RecoverExpiredSessionAsync(
								exception.AuthenticationGeneration, timeout.Token).ConfigureAwait(false);
						}
					} catch when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested) {
						attempt++;
						if (_retryPolicy == RetryPolicy.Progressive) {
							multiplier++;
						}
						await Task.Delay(TimeSpan.FromSeconds(delaySeconds * multiplier), cancellationToken)
							.ConfigureAwait(false);
					}
				}
			}
			throw new InvalidOperationException("The HTTP retry loop completed without a response.");
		}

		private static HttpRequestMessage CreateJsonRequest(HttpMethod method, string url, string requestData,
			bool omitEmptyContent = false)
		{
			HttpRequestMessage request = new HttpRequestMessage(method, url);
			if (!omitEmptyContent || !string.IsNullOrEmpty(requestData)) {
				request.Content = new StringContent(requestData ?? string.Empty, Encoding.UTF8, "application/json");
			}
			return request;
		}

		private static HttpRequestMessage CreateImageUploadRequest(string url, byte[] data,
			string fileName, string mimeType)
		{
			HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url) {
				Content = new ByteArrayContent(data)
			};
			request.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
			request.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, data.LongLength - 1,
				data.LongLength);
			request.Content.Headers.TryAddWithoutValidation("Content-Disposition",
				$"attachment; filename={Uri.EscapeDataString(fileName)}");
			return request;
		}

		private static Cookie CloneCookie(Cookie source, Uri appUri)
		{
			Cookie copy = new Cookie(source.Name, source.Value, string.IsNullOrEmpty(source.Path) ? "/" : source.Path) {
				Domain = string.IsNullOrEmpty(source.Domain) ? appUri.Host : source.Domain,
				Expires = source.Expires,
				HttpOnly = source.HttpOnly,
				Secure = source.Secure
			};
			if (!string.IsNullOrEmpty(source.Domain)) {
				string domain = source.Domain.TrimStart('.');
				if (!appUri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase)
					&& !appUri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) {
					throw new ArgumentException("A session cookie cannot target a host outside the configured Creatio application.",
						nameof(source));
				}
			}
			return copy;
		}

		private static CancellationTokenSource CreateTimeout(int timeoutMilliseconds,
			CancellationToken cancellationToken)
		{
			CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			if (timeoutMilliseconds != Timeout.Infinite) {
				source.CancelAfter(timeoutMilliseconds);
			}
			return source;
		}

		private static HttpResponseMessage CreateEmptyResponse() =>
			new HttpResponseMessage(HttpStatusCode.NoContent) {
				Content = new StringContent(string.Empty)
			};

		private static ICredentials CreateScopedCredentials(ICredentials credentials, Uri appUri)
		{
			if (credentials == null) {
				return null;
			}
			CredentialCache cache = new CredentialCache();
			AddScopedCredentials(cache, credentials, appUri);
			if (appUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) {
				UriBuilder secure = new UriBuilder(appUri) { Scheme = Uri.UriSchemeHttps, Port = 443 };
				AddScopedCredentials(cache, credentials, secure.Uri);
			}
			return cache;
		}

		private static void AddScopedCredentials(CredentialCache cache, ICredentials credentials, Uri uri)
		{
			NetworkCredential negotiate = credentials.GetCredential(uri, "Negotiate");
			NetworkCredential ntlm = credentials.GetCredential(uri, "NTLM");
			if (negotiate != null) {
				cache.Add(uri, "Negotiate", negotiate);
			}
			if (ntlm != null) {
				cache.Add(uri, "NTLM", ntlm);
			}
		}

		private void ThrowIfDisposed()
		{
			if (_disposed) {
				throw new ObjectDisposedException(nameof(CreatioClient));
			}
		}

		private void EnsureLegacyAuthentication() => ExecuteLegacyWebRequest(() =>
			EnsureAuthenticatedAsync(100_000, CancellationToken.None).GetAwaiter().GetResult());

		private static void ExecuteLegacyWebRequest(Action action)
		{
			try {
				action();
			} catch (CreatioAuthenticationHttpException exception) {
				using (exception.Response) {
					throw CreateLegacyProtocolException(exception.Response, exception);
				}
			} catch (OperationCanceledException exception) {
				throw new WebException(exception.Message, exception, WebExceptionStatus.Timeout, null);
			} catch (HttpRequestException exception) {
				throw new WebException(exception.Message, exception, WebExceptionStatus.ConnectFailure, null);
			}
		}

		private static void EnsureLegacySuccess(HttpResponseMessage response)
		{
			if (!response.IsSuccessStatusCode) {
				throw CreateLegacyProtocolException(response);
			}
		}

		private static WebException CreateLegacyProtocolException(HttpResponseMessage response,
			Exception innerException = null) =>
			new WebException(
				$"The remote server returned an error: ({(int)response.StatusCode}) {response.ReasonPhrase}.",
				innerException, WebExceptionStatus.ProtocolError, LegacyHttpWebResponse.Create(response));

		[SuppressMessage("Reliability", "S3453:Classes should not have only private constructors",
			Justification = "Instances bypass HttpWebResponse constructors to preserve the legacy concrete response contract without HttpWebRequest.")]
		private sealed class LegacyHttpWebResponse : HttpWebResponse
		{
			private byte[] _body;
			private WebHeaderCollection _headers;
			private Uri _responseUri;
			private HttpStatusCode _statusCode;
			private string _statusDescription;
			private string _method;

			#pragma warning disable 0618, SYSLIB0051
			private LegacyHttpWebResponse(SerializationInfo info, StreamingContext context)
				: base(info, context) { }
			#pragma warning restore 0618, SYSLIB0051

			internal static LegacyHttpWebResponse Create(HttpResponseMessage response)
			{
				LegacyHttpWebResponse result = (LegacyHttpWebResponse)
					FormatterServices.GetUninitializedObject(typeof(LegacyHttpWebResponse));
				result._body = response.Content?.ReadAsByteArrayAsync().GetAwaiter().GetResult()
					?? Array.Empty<byte>();
				result._headers = new WebHeaderCollection();
				result._responseUri = response.RequestMessage?.RequestUri;
				result._statusCode = response.StatusCode;
				result._statusDescription = response.ReasonPhrase;
				result._method = response.RequestMessage?.Method.Method;
				foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers) {
					result._headers[header.Key] = string.Join(", ", header.Value);
				}
				if (response.Content != null) {
					foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers) {
						result._headers[header.Key] = string.Join(", ", header.Value);
					}
				}
				return result;
			}

			public override long ContentLength {
				get => _body.LongLength;
				set => throw new NotSupportedException();
			}

			public override string ContentType {
				get => _headers[HttpResponseHeader.ContentType];
				set => throw new NotSupportedException();
			}

			public override WebHeaderCollection Headers => _headers;
			public override string Method => _method;
			public override Uri ResponseUri => _responseUri;
			public override HttpStatusCode StatusCode => _statusCode;
			public override string StatusDescription => _statusDescription;
			public override bool SupportsHeaders => true;
			public override Stream GetResponseStream() => new MemoryStream(_body, writable: false);
			public override void Close()
			{
				// The compatibility response owns only immutable managed buffers, so there is nothing to release.
			}
		}

		private static string ReadLegacyServiceResponse(Task<HttpResponseMessage> responseTask)
		{
			try {
				return ReadResponseBody(responseTask);
			} catch (AggregateException exception) when (exception.InnerException is HttpRequestException
				|| exception.InnerException is OperationCanceledException) {
				return string.Empty;
			}
		}

		private void StartListeningSignalR(CancellationToken cancellationToken){
			Thread thread = new Thread(() => {
				IWsListener ws = new WsListenerSignalR(AppUrl, this, cancellationToken);
				ws.MessageReceived += (sender, message) => { MessageReceived?.Invoke(sender, message); };
				ws.ConnectionStateChanged += (sender, state) => { ConnectionStateChanged?.Invoke(sender, state); };
				ws.StartListening();
				ws.Dispose();
			});
			thread.Start();
		}

		private void StartListeningNetFrameworkApp(CancellationToken cancellationToken){
			Thread thread = new Thread(() => {
				IWsListener ws = new WsListenerNetFramework(AppUrl, this, cancellationToken);
				ws.MessageReceived += (sender, message) => { MessageReceived?.Invoke(sender, message); };
				ws.ConnectionStateChanged += (sender, state) => { ConnectionStateChanged?.Invoke(sender, state); };
				ws.StartListening();
				ws.Dispose();
			});
			thread.Start();
		}

		private HttpRequestMessage CreateUploadRequestMessage(Uri uri, byte[] buffer, long totalBytesRead,
				int chunkSize, long totalLength, string fileName, string mime) {
			var msg = new HttpRequestMessage();
			msg.Method = HttpMethod.Post;
			msg.Content = new ByteArrayContent(buffer);
			msg.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
			msg.Content.Headers.ContentRange = new ContentRangeHeaderValue(totalBytesRead, totalBytesRead + chunkSize - 1, totalLength);
			msg.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") {
				FileName = fileName,
			};
			msg.RequestUri = uri;
			return msg;
		}

		#endregion

		#region Methods: Protected

		protected virtual void OnMessageReceived(IEnumerable<WsMessage> messages){
			messages.ToList().ForEach(m => MessageReceived?.Invoke(this, m));
		}

		#endregion

		#region Constructors: Private

		private CreatioClient(string appUrl, bool isNetCore = false){
			AppUrl = appUrl;
			_isNetCore = isNetCore;
		}

		#endregion

		#region Constructors: Public

		/// <summary>
		/// Initializes a new instance of the <see cref="CreatioClient"/> class.
		/// </summary>
		/// <param name="appUrl">The URL of the Creatio application.</param>
		/// <param name="userName">The username to use for authentication.</param>
		/// <param name="userPassword">The password to use for authentication.</param>
		/// <param name="isNetCore">Optional. A boolean value indicating whether the client is running on .NET Core. Default is false.</param>
		public CreatioClient(string appUrl, string userName, string userPassword, bool isNetCore = false){
			AppUrl = appUrl;
			_userName = userName;
			_userPassword = userPassword;
			_isNetCore = isNetCore;
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="CreatioClient"/> class with an explicit login time zone offset.
		/// </summary>
		/// <param name="appUrl">The URL of the Creatio application.</param>
		/// <param name="userName">The username to use for authentication.</param>
		/// <param name="userPassword">The password to use for authentication.</param>
		/// <param name="timeZoneOffset">The UTC-minus-local offset in minutes, matching JavaScript <c>Date.getTimezoneOffset()</c>.</param>
		/// <param name="isNetCore">Optional. A boolean value indicating whether the client is running on .NET Core. Default is false.</param>
		public CreatioClient(string appUrl, string userName, string userPassword, int timeZoneOffset,
			bool isNetCore = false) : this(appUrl, userName, userPassword, isNetCore) {
			TimeZoneOffset = timeZoneOffset;
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="CreatioClient"/> class.
		/// </summary>
		/// <param name="appUrl">The URL of the Creatio application.</param>
		/// <param name="userName">The username to use for authentication.</param>
		/// <param name="userPassword">The password to use for authentication.</param>
		/// <param name="useUntrustedSsl">A boolean value indicating whether to use untrusted SSL.</param>
		/// <param name="isNetCore">Optional. A boolean value indicating whether the client is running on .NET Core. Default is false.</param>
		public CreatioClient(string appUrl, string userName, string userPassword, bool useUntrustedSsl, bool isNetCore = false){
			AppUrl = appUrl;
			_userName = userName;
			_userPassword = userPassword;
			_useUntrustedSsl = useUntrustedSsl;
			_isNetCore = isNetCore;
		}
		
		/// <summary>
		/// Initializes a new instance of the <see cref="CreatioClient"/> class with NTLM authentication.
		/// </summary>
		/// <param name="appUrl">The URL of the Creatio application.</param>
		/// <param name="useUntrustedSsl">A boolean value indicating whether to use untrusted SSL.</param>
		/// <param name="credentials">The credentials to use for NTLM authentication.</param>
		/// <param name="isNetCore">Optional. A boolean value indicating whether the client is running on .NET Core. Default is false.</param>
		/// <example>
		/// <code>
		/// string appUrl = "https://someName.creatio.com";
		/// CreatioClient client = new(appUrl, true, CredentialCache.DefaultNetworkCredentials);
		/// </code>
		/// </example>
		public CreatioClient(string appUrl, bool useUntrustedSsl, ICredentials credentials, bool isNetCore = false){
			_credentials = credentials;
			AppUrl = appUrl;
			_isNetCore = isNetCore;
			_useUntrustedSsl = useUntrustedSsl;
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="CreatioClient"/> class using an existing OAuth bearer token.
		/// No Login call will be made; the token is sent as Authorization: Bearer &lt;token&gt; on every request.
		/// </summary>
		/// <param name="appUrl">The URL of the Creatio application.</param>
		/// <param name="bearerToken">The bearer token. May be passed with or without the leading "Bearer " prefix; the prefix is stripped.</param>
		/// <param name="isNetCore">Optional. A boolean value indicating whether the client is running on .NET Core. Default is false.</param>
		public CreatioClient(string appUrl, string bearerToken, bool isNetCore = false){
			AppUrl = appUrl;
			_isNetCore = isNetCore;
			_oauthToken = StripBearerPrefix(bearerToken);
		}

		private static string StripBearerPrefix(string token){
			if (string.IsNullOrWhiteSpace(token)) {
				return token;
			}
			const string prefix = "Bearer ";
			return token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
				? token.Substring(prefix.Length).Trim()
				: token.Trim();
		}

		#endregion

		#region Methods: Public

		/// <summary>
		/// Returns detached copies of the cookies applicable to the configured Creatio application.
		/// Cookie values are authentication secrets and must not be logged or persisted without protection.
		/// </summary>
		public IReadOnlyList<Cookie> ExportSessionCookies()
		{
			ThrowIfDisposed();
			Uri appUri = new Uri(AppUrl.TrimEnd('/') + "/");
			return _authCookie.GetCookies(appUri).Cast<Cookie>()
				.Select(cookie => CloneCookie(cookie, appUri))
				.ToArray();
		}

		/// <summary>
		/// Imports cookies for the configured Creatio application so an existing browser or service session
		/// can be reused. Imported cookies are copied and cannot target another host.
		/// </summary>
		/// <param name="cookies">Cookies to import.</param>
		public void ImportSessionCookies(IEnumerable<Cookie> cookies)
		{
			ThrowIfDisposed();
			if (cookies == null) {
				throw new ArgumentNullException(nameof(cookies));
			}
			Uri appUri = new Uri(AppUrl.TrimEnd('/') + "/");
			foreach (Cookie cookie in cookies) {
				if (cookie == null) {
					throw new ArgumentException("Session cookies cannot contain null values.", nameof(cookies));
				}
				_authCookie.Add(appUri, CloneCookie(cookie, appUri));
			}
		}

		public static CreatioClient CreateOAuth20Client(string app, string authApp, string clientId,
			string clientSecret,
			bool isNetCore = false){
			CreatioClient client = new CreatioClient(app, isNetCore) {
				_oauthToken = GetAccessTokenByClientCredentials(authApp, clientId, clientSecret).Result
			};
			return client;
		}

		public string CallConfigurationService(string serviceName,
			string serviceMethod,
			string requestData,
			int requestTimeout = 100000){
			EnsureLegacyAuthentication();
			return ReadResponseBody(CallConfigurationServiceAsync(serviceName, serviceMethod, requestData,
				requestTimeout, CancellationToken.None));
		}

		public Task<HttpResponseMessage> CallConfigurationServiceAsync(string serviceName,
			string serviceMethod, string requestData, int requestTimeout = 100000,
			CancellationToken cancellationToken = default(CancellationToken)) =>
			ExecutePostRequestAsync(CreateConfigurationServiceUrl(serviceName, serviceMethod), requestData,
				requestTimeout, _maxAttempts, _delaySec, cancellationToken);

		public void DownloadFile(string url, string filePath, string requestData, int requestTimeout = 100000){
			ExecuteLegacyWebRequest(() => {
				using (HttpResponseMessage response = DownloadFileAsync(url, filePath, requestData, requestTimeout,
					CancellationToken.None).GetAwaiter().GetResult()) {
					EnsureLegacySuccess(response);
				}
			});
		}

		public Task<HttpResponseMessage> DownloadFileAsync(string url, string filePath, string requestData,
			int requestTimeout = 100000, CancellationToken cancellationToken = default(CancellationToken)) =>
			DownloadToFileAsync(HttpMethod.Post, url, filePath, requestData, requestTimeout, cancellationToken);

		public void DownloadFileByGet(string url, string filePath, int requestTimeout = 100000) {
			ExecuteLegacyWebRequest(() => {
				using (HttpResponseMessage response = DownloadFileByGetAsync(url, filePath, requestTimeout,
					CancellationToken.None).GetAwaiter().GetResult()) {
					EnsureLegacySuccess(response);
				}
			});
		}

		public Task<HttpResponseMessage> DownloadFileByGetAsync(string url, string filePath,
			int requestTimeout = 100000, CancellationToken cancellationToken = default(CancellationToken)) =>
			DownloadToFileAsync(HttpMethod.Get, url, filePath, null, requestTimeout, cancellationToken);

		public string ExecuteGetRequest(string url, int requestTimeout = 100000, int maxAttempts = 1, int delaySec = 1) {
			EnsureLegacyAuthentication();
			try {
				return ReadResponseBody(SendAsync(
					() => CreateJsonRequest(HttpMethod.Get, url, null, omitEmptyContent: true),
					requestTimeout, 1, delaySec, CancellationToken.None), unwrapTaskException: true);
			} catch (HttpRequestException) {
				return string.Empty;
			} catch (TaskCanceledException) {
				return string.Empty;
			}
		}

		public Task<HttpResponseMessage> ExecuteGetRequestAsync(string url, int requestTimeout = 100000,
			int maxAttempts = 1, int delaySec = 1,
			CancellationToken cancellationToken = default(CancellationToken)) =>
			SendAsync(() => CreateJsonRequest(HttpMethod.Get, url, null, omitEmptyContent: true),
				requestTimeout, maxAttempts, delaySec, cancellationToken);

		public string ExecutePostRequest(string url, string requestData, int requestTimeout = 10000, int maxAttempts = 1, int delaySec = 1){
			EnsureLegacyAuthentication();
			return ReadResponseBody(ExecutePostRequestAsync(url, requestData, requestTimeout, maxAttempts,
				delaySec, CancellationToken.None));
		}

		public Task<HttpResponseMessage> ExecutePostRequestAsync(string url, string requestData,
			int requestTimeout = 100000, int maxAttempts = 1, int delaySec = 1,
			CancellationToken cancellationToken = default(CancellationToken))
		{
			if (requestData == null) {
				throw new ArgumentNullException(nameof(requestData));
			}
			return SendAsync(() => CreateJsonRequest(HttpMethod.Post, url, requestData), requestTimeout,
				maxAttempts, delaySec, cancellationToken);
		}

		public string ExecuteDeleteRequest(string url, string requestData, int requestTimeout = 10000,
			int maxAttempts = 1, int delaySec = 1)
		{
			EnsureLegacyAuthentication();
			return ReadResponseBody(ExecuteDeleteRequestAsync(url, requestData, requestTimeout, maxAttempts,
				delaySec, CancellationToken.None));
		}

		public Task<HttpResponseMessage> ExecuteDeleteRequestAsync(string url, string requestData,
			int requestTimeout = 10000, int maxAttempts = 1, int delaySec = 1,
			CancellationToken cancellationToken = default(CancellationToken)) =>
			SendAsync(() => CreateJsonRequest(HttpMethod.Delete, url, requestData, omitEmptyContent: true),
				requestTimeout, maxAttempts, delaySec, cancellationToken);

		public string ExecutePatchRequest(string url, string requestData, int requestTimeout = 10000, int maxAttempts = 1, int delaySec = 1){
			EnsureLegacyAuthentication();
			return ReadResponseBody(ExecutePatchRequestAsync(url, requestData, requestTimeout, maxAttempts,
				delaySec, CancellationToken.None));
		}

		public Task<HttpResponseMessage> ExecutePatchRequestAsync(string url, string requestData,
			int requestTimeout = 100000, int maxAttempts = 1, int delaySec = 1,
			CancellationToken cancellationToken = default(CancellationToken)) =>
			SendAsync(() => CreateJsonRequest(new HttpMethod("PATCH"), url, requestData), requestTimeout,
				maxAttempts, delaySec, cancellationToken);

		public string ExecutePutRequest(string url, string requestData, int requestTimeout = 10000, int maxAttempts = 1, int delaySec = 1){
			EnsureLegacyAuthentication();
			return ReadResponseBody(ExecutePutRequestAsync(url, requestData, requestTimeout, maxAttempts,
				delaySec, CancellationToken.None));
		}

		public Task<HttpResponseMessage> ExecutePutRequestAsync(string url, string requestData,
			int requestTimeout = 100000, int maxAttempts = 1, int delaySec = 1,
			CancellationToken cancellationToken = default(CancellationToken)) =>
			SendAsync(() => CreateJsonRequest(HttpMethod.Put, url, requestData), requestTimeout,
				maxAttempts, delaySec, cancellationToken);

		private static string ReadResponseBody(Task<HttpResponseMessage> responseTask,
			bool unwrapTaskException = false)
		{
			HttpResponseMessage response = unwrapTaskException
				? responseTask.GetAwaiter().GetResult()
				: responseTask.Result;
			using (response) {
				return unwrapTaskException
					? response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
					: response.Content.ReadAsStringAsync().Result;
			}
		}

		private async Task<HttpResponseMessage> DownloadToFileAsync(HttpMethod method, string url, // NOSONAR: retry/streaming branches are kept together and fully characterized.
			string filePath, string requestData, int requestTimeout, CancellationToken cancellationToken)
		{
			await EnsureAuthenticatedAsync(100_000, cancellationToken).ConfigureAwait(false);
			int maxAttempts = Math.Max(1, _maxAttempts);
			int multiplier = 1;
			SessionRecoveryState sessionRecovery = new SessionRecoveryState(authenticationComplete: true);
			for (int attempt = 1; attempt <= maxAttempts; attempt++) {
				using (CancellationTokenSource timeout = CreateTimeout(requestTimeout, cancellationToken)) {
					HttpResponseMessage response = null;
					try {
						response = await SendAsync(
							() => CreateJsonRequest(method, url, requestData, omitEmptyContent: method == HttpMethod.Get),
							Timeout.Infinite, 1, _delaySec, timeout.Token,
							HttpCompletionOption.ResponseHeadersRead,
							sessionRecovery: sessionRecovery)
							.ConfigureAwait(false);
						if (!response.IsSuccessStatusCode) {
							if (attempt == maxAttempts) {
								await BufferResponseContentAsync(response, timeout.Token).ConfigureAwait(false);
								return response;
							}
							response.Dispose();
							response = null;
						} else {
							Stream content = await response.Content.ReadAsStreamAsync().ConfigureAwait(false); // NOSONAR: netstandard2.0 has no token overload; reads below use the timeout token.
							using (FileStream stream = new FileStream(filePath, FileMode.Create, FileAccess.Write,
								FileShare.None, 81920, true)) {
								byte[] buffer = new byte[81920];
								int read;
								while ((read = await content.ReadAsync(buffer, 0, buffer.Length, timeout.Token)
									.ConfigureAwait(false)) != 0) {
									await stream.WriteAsync(buffer, 0, read, timeout.Token).ConfigureAwait(false);
								}
							}
							return response;
						}
					} catch when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested) {
						response?.Dispose();
					} catch {
						response?.Dispose();
						throw;
					}
				}
				if (_retryPolicy == RetryPolicy.Progressive) {
					multiplier++;
				}
				await Task.Delay(TimeSpan.FromSeconds(_delaySec * multiplier), cancellationToken)
					.ConfigureAwait(false);
			}
			throw new InvalidOperationException("The download retry loop completed without a response.");
		}

		private sealed class SessionRecoveryState
		{
			public SessionRecoveryState(bool authenticationComplete = false)
			{
				AuthenticationComplete = authenticationComplete;
			}

			public bool AuthenticationComplete { get; }

			public bool Attempted { get; set; }
		}

		private static async Task BufferResponseContentAsync(HttpResponseMessage response,
			CancellationToken cancellationToken)
		{
			if (response.Content == null) {
				return;
			}
			HttpContent original = response.Content;
			List<KeyValuePair<string, IEnumerable<string>>> headers = original.Headers.ToList();
			using (Stream source = await original.ReadAsStreamAsync().ConfigureAwait(false)) // NOSONAR: netstandard2.0 has no token overload; reads below use cancellationToken.
			using (MemoryStream buffer = new MemoryStream()) {
				byte[] bytes = new byte[81920];
				int read;
				while ((read = await source.ReadAsync(bytes, 0, bytes.Length, cancellationToken)
					.ConfigureAwait(false)) != 0) {
					await buffer.WriteAsync(bytes, 0, read, cancellationToken).ConfigureAwait(false);
				}
				ByteArrayContent buffered = new ByteArrayContent(buffer.ToArray());
				foreach (KeyValuePair<string, IEnumerable<string>> header in headers) {
					buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);
				}
				response.Content = buffered;
			}
			original.Dispose();
		}

		public void Login(){
			Login(100_000);
		}

		public void Login(int requestTimeout){
			ExecuteLegacyWebRequest(() => {
				using (HttpResponseMessage response = LoginAsync(requestTimeout, CancellationToken.None)
					.GetAwaiter().GetResult()) {
					EnsureLegacySuccess(response);
				}
			});
		}

		public Task<HttpResponseMessage> LoginAsync(int requestTimeout = 100000,
			CancellationToken cancellationToken = default(CancellationToken))
		{
			_ = HttpClient;
			return _authenticationHandler.LoginAsync(requestTimeout, cancellationToken);
		}

		public void StartListening(CancellationToken cancellationToken){
			if (_isNetCore) {
				StartListeningSignalR(cancellationToken);
			} else {
				StartListeningNetFrameworkApp(cancellationToken);
			}
		}

		public string UploadAlmFileByChunk(string url, string filePath) {
			FileInfo fileInfo = new FileInfo(filePath);
			string result = "";
			using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read)) {
				int chunkSize = 1024*1024;
				byte[] buffer = new byte[chunkSize];
				int bytesRead = 0;
				var fileLenght = (int)fileInfo.Length;
				var downloadedSize = 0;
				while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) != 0) {
					byte[] readedBytes = new byte[bytesRead];
					Array.Copy(buffer, readedBytes, bytesRead);
					result = UploadChunkAlmFile(url, readedBytes, downloadedSize, fileLenght);
					if (result.ToLower().Contains("\"success\": false")) {
						Console.WriteLine($"Error: {result}");
					};
					downloadedSize += bytesRead ;
					var leftByteSize = fileLenght - downloadedSize;
					chunkSize = leftByteSize < chunkSize ? leftByteSize : chunkSize;
					buffer = new byte[chunkSize];
					Console.WriteLine($"Loaded {downloadedSize} from {fileLenght}");
				}
			}
			return result;
		}

		public async Task<HttpResponseMessage> UploadAlmFileByChunkAsync(string url, string filePath,
			int requestTimeout = 100000, CancellationToken cancellationToken = default(CancellationToken))
		{
			FileInfo fileInfo = new FileInfo(filePath);
			int totalSize = checked((int)fileInfo.Length);
			int uploadedSize = 0;
			HttpResponseMessage lastResponse = null;
			using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
				FileShare.Read, 81920, true)) {
				byte[] buffer = new byte[1024 * 1024];
				int bytesRead;
				while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
					.ConfigureAwait(false)) != 0) {
					byte[] chunk = new byte[bytesRead];
					Array.Copy(buffer, chunk, bytesRead);
					HttpResponseMessage response = await UploadChunkAlmFileAsync(url, chunk, uploadedSize,
						totalSize, requestTimeout, cancellationToken).ConfigureAwait(false);
					lastResponse?.Dispose();
					lastResponse = response;
					uploadedSize += bytesRead;
					if (!response.IsSuccessStatusCode) {
						return response;
					}
				}
			}
			return lastResponse ?? CreateEmptyResponse();
		}

		/// <summary>
		/// Uploads one complete image payload to a Creatio Image API URL. The URL carries the Image API
		/// query parameters; this method supplies the byte body and browser-compatible content headers.
		/// </summary>
		/// <param name="url">Fully resolved Image API upload URL.</param>
		/// <param name="data">Complete image bytes.</param>
		/// <param name="fileName">File name including its image extension.</param>
		/// <param name="mimeType">Image MIME type.</param>
		/// <param name="requestTimeout">Request timeout in milliseconds.</param>
		/// <param name="cancellationToken">Cancellation token.</param>
		/// <returns>The response returned by the Image API. The caller owns and must dispose it.</returns>
		public Task<HttpResponseMessage> UploadImageAsync(string url, byte[] data, string fileName,
			string mimeType, int requestTimeout = 100_000,
			CancellationToken cancellationToken = default(CancellationToken))
		{
			if (string.IsNullOrWhiteSpace(url)) {
				throw new ArgumentException("The image upload URL cannot be empty.", nameof(url));
			}
			if (data == null) {
				throw new ArgumentNullException(nameof(data));
			}
			if (data.Length == 0) {
				throw new ArgumentException("The image payload cannot be empty.", nameof(data));
			}
			if (string.IsNullOrWhiteSpace(fileName)) {
				throw new ArgumentException("The image file name cannot be empty.", nameof(fileName));
			}
			if (string.IsNullOrWhiteSpace(mimeType)) {
				throw new ArgumentException("The image MIME type cannot be empty.", nameof(mimeType));
			}
			return SendAsync(() => CreateImageUploadRequest(url, data, fileName, mimeType), requestTimeout,
				1, _delaySec, cancellationToken);
		}

		public string UploadChunkAlmFile(string url, byte[] data, int downloadedSize, int totalSize) {
			EnsureLegacyAuthentication();
			return ReadLegacyServiceResponse(UploadChunkAlmFileAsync(url, data, downloadedSize, totalSize,
				100_000, CancellationToken.None));
		}

		public Task<HttpResponseMessage> UploadChunkAlmFileAsync(string url, byte[] data, int downloadedSize,
			int totalSize, int requestTimeout = 100000,
			CancellationToken cancellationToken = default(CancellationToken))
		{
			int startByte = downloadedSize == 0 ? 0 : downloadedSize + 1;
			return SendAsync(() => {
				HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url) {
					Content = new ByteArrayContent(data)
				};
				request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
				request.Content.Headers.TryAddWithoutValidation("Content-Range",
					$"bytes {startByte}-{downloadedSize + data.Length}/{totalSize}");
				return request;
			}, requestTimeout, _maxAttempts, _delaySec, cancellationToken);
		}

		public string UploadAlmFile(string url, string filePath){
			EnsureLegacyAuthentication();
			return ReadLegacyServiceResponse(UploadAlmFileAsync(url, filePath, 100_000, CancellationToken.None));
		}

		public async Task<HttpResponseMessage> UploadAlmFileAsync(string url, string filePath,
			int requestTimeout = 100000, CancellationToken cancellationToken = default(CancellationToken))
		{
			return await SendAsync(() => {
				FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
					FileShare.Read, 81920, true);
				HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url) {
					Content = new StreamContent(stream)
				};
				request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
				return request;
			}, requestTimeout, _maxAttempts, _delaySec, cancellationToken).ConfigureAwait(false);
		}

		private string GetMimeTypeFromFileExtension(string fileExtension){
			string mimeType = string.Empty;
			switch (fileExtension.ToLower(CultureInfo.InvariantCulture)) {
				case ".zip":
					mimeType = "application/x-zip-compressed";
					break;
				case ".gz":
					mimeType = "application/gzip";
					break;
				case ".json":
					mimeType = "application/json";
					break;
				case ".xml":
					mimeType = "application/xml";
					break;
				case ".jpg":
				case ".jpeg":
					mimeType = "image/jpeg";
					break;
				case ".png":
					mimeType = "image/png";
					break;
				case ".gif":
					mimeType = "image/gif";
					break;
				case ".bmp":
					mimeType = "image/bmp";
					break;
				case ".tiff":
				case ".tif":
					mimeType = "image/tiff";
					break;
				case ".webp":
					mimeType = "image/webp";
					break;
				case ".svg":
					mimeType = "image/svg+xml";
					break;
				case ".dll":
					mimeType = "application/x-msdownload";
					break;
				default:
					return "application/octet-stream";
			}
			return mimeType;
		}
		
		public string UploadStaticFile(string url, string filePath, string folderName, int defaultTimeout = 100_000, int chunkSize = 1 * 1024 * 1024){
			EnsureLegacyAuthentication();
			return UploadStaticFileAsync(url, filePath, folderName, defaultTimeout).ConfigureAwait(false).GetAwaiter().GetResult();
		}
		
		public async Task<string> UploadStaticFileAsync(string url, string filePath, string folderName, int defaultTimeout = 100_000, int chunkSize = 30 * 1024 * 1024){
			using (HttpResponseMessage response = await UploadStaticFileAsync(url, filePath, folderName,
				defaultTimeout, chunkSize, CancellationToken.None).ConfigureAwait(false)) {
				string result = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				response.EnsureSuccessStatusCode();
				return result;
			}
		}

		public Task<HttpResponseMessage> UploadStaticFileAsync(string url, string filePath, string folderName,
			int defaultTimeout, int chunkSize, CancellationToken cancellationToken) =>
			UploadChunksAsync(filePath, defaultTimeout, chunkSize, cancellationToken,
				(fileName, mime, length) => url + "&fileName=" + fileName + $"folderName={folderName}");

		public string UploadFile(string url, string filePath, int defaultTimeout = 100_000, int chunkSize = 1 * 1024 * 1024){
			EnsureLegacyAuthentication();
			return UploadFileAsync(url, filePath, defaultTimeout).ConfigureAwait(false).GetAwaiter().GetResult();
		}
		public async Task<string> UploadFileAsync(string url, string filePath, int defaultTimeout = 100_000, int chunkSize = 1 * 1024 * 1024){
			using (HttpResponseMessage response = await UploadFileAsync(url, filePath, defaultTimeout,
				chunkSize, CancellationToken.None).ConfigureAwait(false)) {
				string result = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				response.EnsureSuccessStatusCode();
				return result;
			}
		}

		public Task<HttpResponseMessage> UploadFileAsync(string url, string filePath, int defaultTimeout,
			int chunkSize, CancellationToken cancellationToken) =>
			UploadChunksAsync(filePath, defaultTimeout, chunkSize, cancellationToken,
				(fileName, mime, length) => url + "?totalFileLength=" + length +
					"&fileName=" + fileName + $"&mimeType={Uri.EscapeDataString(mime)}");

		private async Task<HttpResponseMessage> UploadChunksAsync(string filePath, int requestTimeout,
			int chunkSize, CancellationToken cancellationToken, Func<string, string, long, string> buildUrl)
		{
			FileInfo fileInfo = new FileInfo(filePath);
			string fileName = fileInfo.Name;
			string mime = GetMimeTypeFromFileExtension(fileInfo.Extension);
			long totalBytesRead = 0;
			HttpResponseMessage lastResponse = null;
			using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
				FileShare.Read, 81920, true)) {
				while (stream.Length > totalBytesRead) {
					int currentChunkSize = (int)Math.Min(chunkSize, stream.Length - totalBytesRead);
					byte[] buffer = new byte[currentChunkSize];
					int bytesRead = await stream.ReadAsync(buffer, 0, currentChunkSize, cancellationToken)
						.ConfigureAwait(false);
					long chunkStart = totalBytesRead;
					totalBytesRead += bytesRead;
					Uri uri = new Uri(buildUrl(fileName, mime, stream.Length));
					HttpResponseMessage response = await SendAsync(
						() => CreateUploadRequestMessage(uri, buffer, chunkStart, bytesRead, stream.Length,
							fileName, mime), requestTimeout, _maxAttempts, _delaySec, cancellationToken)
						.ConfigureAwait(false);
					lastResponse?.Dispose();
					lastResponse = response;
					if (!response.IsSuccessStatusCode) {
						return response;
					}
					string result = await response.Content.ReadAsStringAsync().ConfigureAwait(false); // NOSONAR: SendAsync already buffered content under cancellation.
					HandleUploadResponse(response, result, totalBytesRead, stream.Length);
				}
			}
			return lastResponse ?? CreateEmptyResponse();
		}
		
		public string UploadFile_original(string url, string filePath, int defaultTimeout = 100000){
			EnsureLegacyAuthentication();
			return ReadLegacyServiceResponse(UploadFile_originalAsync(url, filePath, defaultTimeout,
				CancellationToken.None));
		}

		public async Task<HttpResponseMessage> UploadFile_originalAsync(string url, string filePath,
			int defaultTimeout = 100000, CancellationToken cancellationToken = default(CancellationToken))
		{
			FileInfo fileInfo = new FileInfo(filePath);
			string fileName = fileInfo.Name;
			string boundary = DateTime.Now.Ticks.ToString("x");
			using (MemoryStream content = new MemoryStream()) {
				byte[] boundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "\r\n");
				byte[] endBoundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary + "--");
			string headerTemplate =
				"Content-Disposition: form-data; name=\"{0}\"; filename=\"{1}\"\r\n" +
				"Content-Type: application/octet-stream\r\n\r\n";
				await content.WriteAsync(boundaryBytes, 0, boundaryBytes.Length, cancellationToken)
					.ConfigureAwait(false);
				byte[] headerBytes = Encoding.UTF8.GetBytes(string.Format(headerTemplate, "files", fileName));
				await content.WriteAsync(headerBytes, 0, headerBytes.Length, cancellationToken)
					.ConfigureAwait(false);
				using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
					FileShare.Read, 81920, true)) {
					await fileStream.CopyToAsync(content, 81920, cancellationToken).ConfigureAwait(false);
				}
				await content.WriteAsync(endBoundaryBytes, 0, endBoundaryBytes.Length, cancellationToken)
					.ConfigureAwait(false);
				byte[] body = content.ToArray();
				return await SendAsync(() => {
					HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url) {
						Content = new ByteArrayContent(body)
					};
					request.Content.Headers.TryAddWithoutValidation("Content-Type",
						"multipart/form-data; boundary=" + boundary);
					return request;
				}, defaultTimeout, _maxAttempts, _delaySec, cancellationToken).ConfigureAwait(false);
			}
		}

		/// <inheritdoc/>
		public void SetRetryPolicy(int maxAttempts, int delaySec, RetryPolicy retryPolicy) {
			_maxAttempts = maxAttempts;
			_delaySec = delaySec;
			_retryPolicy = retryPolicy;
		}

		/// <inheritdoc/>
		public async Task<string> UploadAttachmentAsync(FileUploadInfo uploadInfo, int timeout = 100000,
			int chunkSize = 1048576) {
			using (HttpResponseMessage response = await UploadAttachmentResponseAsync(uploadInfo, timeout,
				chunkSize, CancellationToken.None).ConfigureAwait(false)) {
				string result = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				response.EnsureSuccessStatusCode();
				return result;
			}
		}

		public async Task<HttpResponseMessage> UploadAttachmentResponseAsync(FileUploadInfo uploadInfo,
			int timeout = 100000, int chunkSize = 1048576,
			CancellationToken cancellationToken = default(CancellationToken))
		{
			ValidateUploadInfo(uploadInfo);
			long totalBytesRead = 0;
			FileInfo fileInfo = new FileInfo(uploadInfo.FilePath);
			string fileName = fileInfo.Name;
			string mime = GetMimeTypeFromFileExtension(fileInfo.Extension);
			string url = CreateConfigurationServiceUrl("FileApiService", "UploadFile");
			Guid fileId = Guid.NewGuid();
			HttpResponseMessage lastResponse = null;
			using (FileStream stream = new FileStream(uploadInfo.FilePath, FileMode.Open, FileAccess.Read,
				FileShare.Read, 81920, true)) {
				while (stream.Length > totalBytesRead) {
					Uri uri = BuildUploadUri(url, stream.Length, fileId, uploadInfo, fileName, mime);
					int currentChunkSize = (int)Math.Min(chunkSize, stream.Length - totalBytesRead);
					byte[] buffer = new byte[currentChunkSize];
					int bytesRead = await stream.ReadAsync(buffer, 0, currentChunkSize, cancellationToken)
						.ConfigureAwait(false);
					long chunkStart = totalBytesRead;
					totalBytesRead += bytesRead;
					HttpResponseMessage response = await SendAsync(
						() => CreateUploadRequestMessage(uri, buffer, chunkStart, bytesRead, stream.Length,
							fileName, mime), timeout, _maxAttempts, _delaySec, cancellationToken)
						.ConfigureAwait(false);
					lastResponse?.Dispose();
					lastResponse = response;
					if (!response.IsSuccessStatusCode) {
						return response;
					}
					string result = await response.Content.ReadAsStringAsync().ConfigureAwait(false); // NOSONAR: SendAsync already buffered content under cancellation.
					HandleUploadResponse(response, result, totalBytesRead, stream.Length);
				}
			}
			return lastResponse ?? CreateEmptyResponse();
		}

		public bool DownloadAttachment(string schemaName, Guid recordId, string filePath, int timeout = 100000) {
			if (string.IsNullOrEmpty(schemaName)) {
				throw new ArgumentException("SchemaName cannot be null or empty", nameof(schemaName));
			}
			if (recordId == Guid.Empty) {
				throw new ArgumentException("RecordId cannot be empty Guid", nameof(recordId));
			}
			if (string.IsNullOrEmpty(filePath)) {
				throw new ArgumentException("FilePath cannot be null or empty", nameof(filePath));
			}
			bool result = false;
			ExecuteLegacyWebRequest(() => {
				using (HttpResponseMessage response = DownloadAttachmentAsync(schemaName, recordId, filePath, timeout,
					CancellationToken.None).GetAwaiter().GetResult()) {
					EnsureLegacySuccess(response);
					result = true;
				}
			});
			return result;
		}

		public Task<HttpResponseMessage> DownloadAttachmentAsync(string schemaName, Guid recordId,
			string filePath, int timeout = 100000,
			CancellationToken cancellationToken = default(CancellationToken))
		{
			if (string.IsNullOrEmpty(schemaName)) {
				throw new ArgumentException("SchemaName cannot be null or empty", nameof(schemaName));
			}
			if (recordId == Guid.Empty) {
				throw new ArgumentException("RecordId cannot be empty Guid", nameof(recordId));
			}
			if (string.IsNullOrEmpty(filePath)) {
				throw new ArgumentException("FilePath cannot be null or empty", nameof(filePath));
			}
			string url = $"{CreateConfigurationServiceUrl("FileService", "Download")}/{schemaName}/{recordId}";
			return DownloadToFileAsync(HttpMethod.Get, url, filePath, null, timeout, cancellationToken);
		}

		public void Dispose()
		{
			lock (_httpClientLock) {
				if (_disposed) {
					return;
				}
				_disposed = true;
				_httpClient?.Dispose();
			}
		}

		#endregion

	}

	#endregion
}
