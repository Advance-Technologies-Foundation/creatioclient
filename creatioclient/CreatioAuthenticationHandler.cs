using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Creatio.Client
{
	internal sealed class CreatioAuthenticationHandler : DelegatingHandler
	{
		private readonly Uri _appUri;
		private readonly CookieContainer _cookies;
		private readonly string _userName;
		private readonly string _userPassword;
		private readonly ICredentials _credentials;
		private readonly Func<string> _bearerToken;
		private readonly Func<int?> _timeZoneOffset;
		private readonly Func<bool> _skipPing;
		private readonly bool _isNetCore;
		private readonly SemaphoreSlim _loginLock = new SemaphoreSlim(1, 1);
		private bool _authenticated;

		public CreatioAuthenticationHandler(Uri appUri, CookieContainer cookies, string userName,
			string userPassword, ICredentials credentials, Func<string> bearerToken,
			Func<int?> timeZoneOffset, Func<bool> skipPing, bool isNetCore, HttpMessageHandler innerHandler)
		{
			_appUri = EnsureTrailingSlash(appUri);
			_cookies = cookies;
			_userName = userName;
			_userPassword = userPassword;
			_credentials = credentials;
			_bearerToken = bearerToken;
			_timeZoneOffset = timeZoneOffset;
			_skipPing = skipPing;
			_isNetCore = isNetCore;
			InnerHandler = innerHandler;
		}

		public async Task<HttpResponseMessage> LoginAsync(int requestTimeout, CancellationToken cancellationToken)
		{
			using (CancellationTokenSource timeout = CreateTimeout(requestTimeout, cancellationToken)) {
				HttpResponseMessage response = _credentials == null
					? await PasswordLoginAsync(timeout.Token).ConfigureAwait(false)
					: await NtlmLoginAsync(timeout.Token).ConfigureAwait(false);
				_authenticated = response.IsSuccessStatusCode && HasSession();
				return response;
			}
		}

		public Task EnsureAuthenticatedForRequestAsync(CancellationToken cancellationToken) =>
			EnsureAuthenticatedAsync(cancellationToken);

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			if (!IsTrustedOrigin(request.RequestUri)) {
				throw new InvalidOperationException("CreatioClient cannot send authenticated requests to a different origin.");
			}
			string token = _bearerToken();
			if (!string.IsNullOrEmpty(token)) {
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			} else {
				await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
				Cookie csrf = _cookies.GetCookies(_appUri)["BPMCSRF"];
				if (csrf != null && !request.Headers.Contains("BPMCSRF")) {
					request.Headers.TryAddWithoutValidation("BPMCSRF", csrf.Value);
				}
			}
			return await SendInnerAsync(request, cancellationToken).ConfigureAwait(false);
		}

		private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
		{
			if (!string.IsNullOrEmpty(_bearerToken())) {
				return;
			}
			if (_authenticated || HasSession()) {
				_authenticated = true;
				return;
			}
			await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
			try {
				if (_authenticated || HasSession()) {
					_authenticated = true;
					return;
				}
				using (HttpResponseMessage response = _credentials == null
					? await PasswordLoginAsync(cancellationToken).ConfigureAwait(false)
					: await NtlmLoginAsync(cancellationToken).ConfigureAwait(false)) {
					if (!response.IsSuccessStatusCode) {
						throw new CreatioAuthenticationHttpException(CloneResponse(response));
					}
					if (_credentials == null && !HasSession()) {
						throw new UnauthorizedAccessException(
							$"Authentication response for {_appUri.ToString().TrimEnd('/')} did not contain an auth cookie.");
					}
					_authenticated = true;
				}
				if (!_isNetCore && !_skipPing()) {
					await TryPingAsync(cancellationToken).ConfigureAwait(false);
				}
			} finally {
				_loginLock.Release();
			}
		}

		private async Task<HttpResponseMessage> PasswordLoginAsync(CancellationToken cancellationToken)
		{
			int offset = _timeZoneOffset() ?? -(int)DateTimeOffset.Now.Offset.TotalMinutes;
			string body = JsonConvert.SerializeObject(new {
				UserName = _userName,
				UserPassword = _userPassword,
				TimeZoneOffset = offset
			});
			using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post,
				new Uri(_appUri, "ServiceModel/AuthService.svc/Login")) {
				Content = new StringContent(body, Encoding.UTF8, "application/json")
			}) {
				HttpResponseMessage response = await SendInnerAsync(request, cancellationToken).ConfigureAwait(false);
				string responseBody = await ReadLoginContentAsync(response, cancellationToken)
					.ConfigureAwait(false);
				if (responseBody.Contains("\"Code\":1")) {
					response.Dispose();
					throw new UnauthorizedAccessException($"Unauthorized {_userName} for {_appUri.ToString().TrimEnd('/')}");
				}
				return response;
			}
		}

		private async Task<HttpResponseMessage> NtlmLoginAsync(CancellationToken cancellationToken)
		{
			using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get,
				new Uri(_appUri, "Login/NuiLogin.aspx?ntlmlogin"))) {
				HttpResponseMessage response = await SendInnerAsync(request, cancellationToken).ConfigureAwait(false);
				_ = await ReadLoginContentAsync(response, cancellationToken).ConfigureAwait(false);
				return response;
			}
		}

		private static async Task<string> ReadLoginContentAsync(HttpResponseMessage response,
			CancellationToken cancellationToken)
		{
			try {
				return await ReadContentWithCancellationAsync(response, cancellationToken).ConfigureAwait(false);
			} catch {
				response.Dispose();
				throw;
			}
		}

		private static HttpResponseMessage CloneResponse(HttpResponseMessage response)
		{
			HttpResponseMessage clone = new HttpResponseMessage(response.StatusCode) {
				ReasonPhrase = response.ReasonPhrase,
				RequestMessage = response.RequestMessage == null
					? null
					: new HttpRequestMessage(response.RequestMessage.Method, response.RequestMessage.RequestUri),
				Content = new ByteArrayContent(response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult())
			};
			foreach (var header in response.Headers) {
				clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
			}
			foreach (var header in response.Content.Headers) {
				clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
			}
			return clone;
		}

		private async Task TryPingAsync(CancellationToken cancellationToken)
		{
			try {
				using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post,
					new Uri(_appUri, "0/ping"))) {
					Cookie csrf = _cookies.GetCookies(_appUri)["BPMCSRF"];
					if (csrf != null) {
						request.Headers.TryAddWithoutValidation("BPMCSRF", csrf.Value);
					}
					using (HttpResponseMessage response = await SendInnerAsync(request, cancellationToken)
						.ConfigureAwait(false)) { }
				}
			} catch (HttpRequestException) {
				// Legacy lazy authentication treats a ping transport failure as non-fatal.
			}
		}

		private bool HasSession()
		{
			CookieCollection cookies = _cookies.GetCookies(_appUri);
			return _credentials == null
				? cookies[".ASPXAUTH"] != null
				: cookies["BPMCSRF"] != null || _authenticated;
		}

		private bool IsSameOrigin(Uri requestUri) => requestUri != null
			&& requestUri.Scheme.Equals(_appUri.Scheme, StringComparison.OrdinalIgnoreCase)
			&& requestUri.Host.Equals(_appUri.Host, StringComparison.OrdinalIgnoreCase)
			&& requestUri.Port == _appUri.Port;

		private bool IsTrustedOrigin(Uri requestUri) => IsSameOrigin(requestUri)
			|| IsSameHostHttpsUpgrade(requestUri);

		private bool IsSameHostHttpsUpgrade(Uri requestUri) => requestUri != null
			&& _appUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
			&& requestUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
			&& requestUri.Host.Equals(_appUri.Host, StringComparison.OrdinalIgnoreCase)
			&& requestUri.Port == 443;

		private static Uri EnsureTrailingSlash(Uri uri) =>
			new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);

		private async Task<HttpResponseMessage> SendInnerAsync(HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			HttpRequestMessage current = request;
			bool ownsCurrent = false;
			try {
				for (int redirectCount = 0; redirectCount < 50; redirectCount++) {
					HttpResponseMessage response = await AwaitWithCancellationAsync(
						base.SendAsync(current, cancellationToken), cancellationToken).ConfigureAwait(false);
					if (!IsRedirect(response.StatusCode) || response.Headers.Location == null) {
						return response;
					}
					Uri location = response.Headers.Location.IsAbsoluteUri
						? response.Headers.Location
						: new Uri(current.RequestUri, response.Headers.Location);
					if (!IsTrustedOrigin(location)) {
						return response;
					}
					HttpRequestMessage redirected = CreateRedirectRequest(current, response.StatusCode, location);
					if (redirected == null) {
						return response;
					}
					response.Dispose();
					if (ownsCurrent) {
						current.Dispose();
					}
					current = redirected;
					ownsCurrent = true;
				}
				throw new HttpRequestException("The request exceeded the maximum of 50 redirects.");
			} finally {
				if (ownsCurrent) {
					current.Dispose();
				}
			}
		}

		private static HttpRequestMessage CreateRedirectRequest(HttpRequestMessage request,
			HttpStatusCode statusCode, Uri location)
		{
			bool convertToGet = statusCode == HttpStatusCode.Moved
				|| statusCode == HttpStatusCode.Redirect
				|| statusCode == HttpStatusCode.RedirectMethod;
			HttpMethod method = convertToGet && request.Method != HttpMethod.Get && request.Method != HttpMethod.Head
				? HttpMethod.Get
				: request.Method;
			if (method == request.Method && request.Content != null) {
				return null;
			}
			HttpRequestMessage redirected = new HttpRequestMessage(method, location) {
				Version = request.Version
			};
			foreach (var header in request.Headers) {
				redirected.Headers.TryAddWithoutValidation(header.Key, header.Value);
			}
			return redirected;
		}

		private static bool IsRedirect(HttpStatusCode statusCode) =>
			statusCode == HttpStatusCode.MultipleChoices
			|| statusCode == HttpStatusCode.Moved
			|| statusCode == HttpStatusCode.Redirect
			|| statusCode == HttpStatusCode.RedirectMethod
			|| (int)statusCode == 307
			|| (int)statusCode == 308;

		private static async Task<HttpResponseMessage> AwaitWithCancellationAsync(
			Task<HttpResponseMessage> responseTask, CancellationToken cancellationToken)
		{
			if (!cancellationToken.CanBeCanceled) {
				return await responseTask.ConfigureAwait(false);
			}
			TaskCompletionSource<bool> canceled = new TaskCompletionSource<bool>(
				TaskCreationOptions.RunContinuationsAsynchronously);
			using (cancellationToken.Register(() => canceled.TrySetResult(true))) {
				if (await Task.WhenAny(responseTask, canceled.Task).ConfigureAwait(false) != responseTask) {
					_ = responseTask.ContinueWith(task => {
						if (task.Status == TaskStatus.RanToCompletion) {
							task.Result.Dispose();
						} else {
							_ = task.Exception;
						}
					}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
					throw new OperationCanceledException(cancellationToken);
				}
			}
			return await responseTask.ConfigureAwait(false);
		}

		private static async Task<string> ReadContentWithCancellationAsync(HttpResponseMessage response,
			CancellationToken cancellationToken)
		{
			Task<string> readTask = response.Content.ReadAsStringAsync(); // NOSONAR: netstandard2.0 has no token overload; cancellation is raced below.
			if (!cancellationToken.CanBeCanceled) {
				return await readTask.ConfigureAwait(false);
			}
			TaskCompletionSource<bool> canceled = new TaskCompletionSource<bool>(
				TaskCreationOptions.RunContinuationsAsynchronously);
			using (cancellationToken.Register(() => canceled.TrySetResult(true))) {
				if (await Task.WhenAny(readTask, canceled.Task).ConfigureAwait(false) != readTask) {
					response.Dispose();
					_ = readTask.ContinueWith(task => _ = task.Exception, CancellationToken.None,
						TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
						TaskScheduler.Default);
					throw new OperationCanceledException(cancellationToken);
				}
			}
			return await readTask.ConfigureAwait(false);
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
	}

	internal sealed class CreatioAuthenticationHttpException : HttpRequestException
	{
		internal CreatioAuthenticationHttpException(HttpResponseMessage response)
			: base($"The authentication endpoint returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.")
		{
			Response = response;
		}

		internal HttpResponseMessage Response { get; }
	}
}
