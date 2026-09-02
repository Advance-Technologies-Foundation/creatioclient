using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Creatio.Client
{
	internal sealed class CreatioAuthenticationHandler : DelegatingHandler
	{
		private const string ModernCsrfCookieName = "CRT_CSRF";
		private const string LegacyCsrfCookieName = "BPMCSRF";

		private readonly Uri _appUri;
		private readonly CookieContainer _cookies;
		private readonly string _userName;
		private readonly string _userPassword;
		private readonly ICredentials _credentials;
		private readonly Func<string> _bearerToken;
		private readonly Func<CancellationToken, Task> _refreshBearerToken;
		private readonly Func<int?> _timeZoneOffset;
		private readonly Func<bool> _skipPing;
		private readonly bool _isNetCore;
		private readonly SemaphoreSlim _loginLock = new SemaphoreSlim(1, 1);
		private readonly ConcurrentDictionary<string, string> _cookieSameSite =
			new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		private bool _authenticated;
		private int _authenticationGeneration;

		public CreatioAuthenticationHandler(Uri appUri, CookieContainer cookies, string userName,
			string userPassword, ICredentials credentials, Func<string> bearerToken,
			Func<int?> timeZoneOffset, Func<bool> skipPing, bool isNetCore, HttpMessageHandler innerHandler,
			Func<CancellationToken, Task> refreshBearerToken = null)
		{
			_appUri = EnsureTrailingSlash(appUri);
			_cookies = cookies;
			_userName = userName;
			_userPassword = userPassword;
			_credentials = credentials;
			_bearerToken = bearerToken;
			_refreshBearerToken = refreshBearerToken;
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
				if (_authenticated) {
					Interlocked.Increment(ref _authenticationGeneration);
				}
				return response;
			}
		}

		public Task EnsureAuthenticatedForRequestAsync(CancellationToken cancellationToken) =>
			EnsureAuthenticatedAsync(cancellationToken);

		public string GetCookieSameSite(string cookieName, string domain, string path) =>
			_cookieSameSite.TryGetValue(BuildCookieMetadataKey(cookieName, domain, path), out string sameSite)
				? sameSite
				: "Lax";

		public void SetCookieSameSite(string cookieName, string domain, string path, string sameSite) =>
			_cookieSameSite[BuildCookieMetadataKey(cookieName, domain, path)] = NormalizeSameSite(sameSite);

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			if (!IsTrustedOrigin(request.RequestUri)) {
				throw new InvalidOperationException("CreatioClient cannot send authenticated requests to a different origin.");
			}
			int authenticationGeneration = Volatile.Read(ref _authenticationGeneration);
			string token = _bearerToken();
			if (!string.IsNullOrEmpty(token)) {
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			} else {
				await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
				authenticationGeneration = Volatile.Read(ref _authenticationGeneration);
				ApplyCsrfHeader(request);
			}
			HttpResponseMessage response = await SendInnerAsync(request, cancellationToken,
				stopAtAuthenticationRedirect: string.IsNullOrEmpty(token)).ConfigureAwait(false);
			if (!string.IsNullOrEmpty(token) && _refreshBearerToken != null
				&& response.StatusCode == HttpStatusCode.Unauthorized) {
				throw new CreatioBearerTokenExpiredException(authenticationGeneration, response);
			}
			if (string.IsNullOrEmpty(token) && IsExpiredSessionResponse(response, authenticationGeneration)) {
				throw new CreatioSessionExpiredException(authenticationGeneration, response);
			}
			return response;
		}

		public async Task RecoverExpiredSessionAsync(int authenticationGeneration,
			CancellationToken cancellationToken)
		{
			await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
			try {
				if (Volatile.Read(ref _authenticationGeneration) != authenticationGeneration) {
					return;
				}
				InvalidateSession();
				await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
			} finally {
				_loginLock.Release();
			}
		}

		public async Task RecoverExpiredBearerTokenAsync(int authenticationGeneration,
			CancellationToken cancellationToken)
		{
			await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
			try {
				if (Volatile.Read(ref _authenticationGeneration) != authenticationGeneration) {
					return;
				}
				await _refreshBearerToken(cancellationToken).ConfigureAwait(false);
				Interlocked.Increment(ref _authenticationGeneration);
			} finally {
				_loginLock.Release();
			}
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
				await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
			} finally {
				_loginLock.Release();
			}
		}

		private async Task AuthenticateAsync(CancellationToken cancellationToken)
		{
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
				Interlocked.Increment(ref _authenticationGeneration);
			}
			if (!_isNetCore && !_skipPing()) {
				await TryPingAsync(cancellationToken).ConfigureAwait(false);
			}
		}

		private void InvalidateSession()
		{
			_authenticated = false;
			HashSet<string> expired = new HashSet<string>(StringComparer.Ordinal);
			ExpireAuthenticationCookies(_appUri, expired);
			ExpireAuthenticationCookies(new Uri(_appUri, "ServiceModel/AuthService.svc/Login"), expired);
		}

		private void InvalidateSession(HttpResponseMessage response)
		{
			InvalidateSession();
			Uri requestUri = new Uri(_appUri, "ServiceModel/AuthService.svc/Login");
			if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string> headers)) {
				return;
			}
			foreach (string header in headers) {
				ExpireAuthenticationCookieFromHeader(requestUri, header);
			}
		}

		private void ExpireAuthenticationCookies(Uri uri, HashSet<string> expired)
		{
			foreach (Cookie cookie in _cookies.GetCookies(uri)) {
				if (cookie.Name != ".ASPXAUTH" && cookie.Name != ModernCsrfCookieName
					&& cookie.Name != LegacyCsrfCookieName) {
					continue;
				}
				string key = BuildCookieMetadataKey(cookie.Name, cookie.Domain, cookie.Path);
				if (expired.Add(key)) {
					cookie.Expired = true;
					_cookieSameSite.TryRemove(key, out _);
				}
			}
		}

		private void ExpireAuthenticationCookieFromHeader(Uri requestUri, string header)
		{
			string[] parts = header.Split(';');
			string[] nameValue = parts[0].Split(new[] { '=' }, 2);
			if (nameValue.Length != 2) {
				return;
			}
			string name = nameValue[0].Trim();
			if (name != ".ASPXAUTH" && name != ModernCsrfCookieName && name != LegacyCsrfCookieName) {
				return;
			}
			string path = null;
			foreach (string part in parts) {
				string attribute = part.Trim();
				if (attribute.StartsWith("Path=", StringComparison.OrdinalIgnoreCase)) {
					path = attribute.Substring("Path=".Length);
					break;
				}
			}
			if (string.IsNullOrEmpty(path)) {
				return;
			}
			Uri probeUri = new Uri(requestUri.GetLeftPart(UriPartial.Authority) + "/" +
				path.Trim('/').TrimEnd('/') + "/clio-cookie-invalidation");
			foreach (Cookie cookie in _cookies.GetCookies(probeUri)) {
				if (cookie.Name == name && cookie.Path == path) {
					cookie.Expired = true;
					_cookieSameSite.TryRemove(BuildCookieMetadataKey(cookie.Name, cookie.Domain, cookie.Path), out _);
				}
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
				string responseBody;
				try {
					responseBody = await ReadLoginContentAsync(response, cancellationToken).ConfigureAwait(false);
				} catch {
					InvalidateSession(response);
					throw;
				}
				if (!response.IsSuccessStatusCode) {
					InvalidateSession(response);
					return response;
				}
				if (!HasSuccessfulLoginCode(responseBody)) {
					InvalidateSession(response);
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
					ApplyCsrfHeader(request);
					using (HttpResponseMessage response = await SendInnerAsync(request, cancellationToken)
						.ConfigureAwait(false)) { }
				}
			} catch (HttpRequestException) {
				// Legacy lazy authentication treats a ping transport failure as non-fatal.
				return;
			}
		}

		private bool HasSession()
		{
			CookieCollection cookies = _cookies.GetCookies(_appUri);
			return _credentials == null
				? cookies[".ASPXAUTH"] != null
				: GetCsrfCookie(cookies) != null || _authenticated;
		}

		private void ApplyCsrfHeader(HttpRequestMessage request)
		{
			Cookie csrf = GetCsrfCookie(_cookies.GetCookies(_appUri));
			if (csrf != null && !string.IsNullOrEmpty(csrf.Value)
				&& !request.Headers.Contains(csrf.Name)) {
				request.Headers.TryAddWithoutValidation(csrf.Name, csrf.Value);
			}
		}

		private static Cookie GetCsrfCookie(CookieCollection cookies)
		{
			Cookie modern = cookies[ModernCsrfCookieName];
			if (modern != null && !string.IsNullOrEmpty(modern.Value)) {
				return modern;
			}
			Cookie legacy = cookies[LegacyCsrfCookieName];
			return legacy != null && !string.IsNullOrEmpty(legacy.Value) ? legacy : null;
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
			CancellationToken cancellationToken, bool stopAtAuthenticationRedirect = false)
		{
			HttpRequestMessage current = request;
			bool ownsCurrent = false;
			try {
				for (int redirectCount = 0; redirectCount < 50; redirectCount++) {
					HttpResponseMessage response = await AwaitWithCancellationAsync(
						base.SendAsync(current, cancellationToken), cancellationToken).ConfigureAwait(false);
			CaptureCookieMetadata(response);
					if (!IsRedirect(response.StatusCode) || response.Headers.Location == null) {
						return response;
					}
					Uri location = response.Headers.Location.IsAbsoluteUri
						? response.Headers.Location
						: new Uri(current.RequestUri, response.Headers.Location);
					if (ShouldStopAtRedirect(location, stopAtAuthenticationRedirect)) {
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

		private void CaptureCookieMetadata(HttpResponseMessage response)
		{
			Uri requestUri = response.RequestMessage?.RequestUri;
			if (requestUri == null
				|| !response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string> headers)) {
				return;
			}
			foreach (string header in headers) {
				CookieContainer parsed = new CookieContainer();
				try {
					parsed.SetCookies(requestUri, header);
				} catch (CookieException) {
					continue;
				}
				string sameSite = ReadSameSite(header);
				CookieCollection actualCookies = _cookies.GetCookies(requestUri);
				foreach (Cookie declared in parsed.GetCookies(requestUri)) {
					foreach (Cookie actual in actualCookies) {
						if (SameCookieIdentity(actual, declared) && actual.Value == declared.Value) {
							SetCookieSameSite(actual.Name, actual.Domain, actual.Path, sameSite);
							break;
						}
					}
				}
			}
		}

		private static bool HasSuccessfulLoginCode(string responseBody)
		{
			try {
				JToken code = JObject.Parse(responseBody)["Code"];
				return code != null && code.Type == JTokenType.Integer
					&& code.ToString(Formatting.None) == "0";
			} catch (JsonException) {
				return false;
			}
		}

		private static string NormalizeSameSite(string value)
		{
			if (string.Equals(value?.Trim(), "Strict", StringComparison.OrdinalIgnoreCase)) {
				return "Strict";
			}
			if (string.Equals(value?.Trim(), "None", StringComparison.OrdinalIgnoreCase)) {
				return "None";
			}
			return "Lax";
		}

		private static string ReadSameSite(string header)
		{
			foreach (string part in header.Split(';')) {
				string attribute = part.Trim();
				if (attribute.StartsWith("SameSite=", StringComparison.OrdinalIgnoreCase)) {
					return attribute.Substring("SameSite=".Length);
				}
			}
			return null;
		}

		private static bool SameCookieIdentity(Cookie left, Cookie right) =>
			string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(left.Domain.TrimStart('.'), right.Domain.TrimStart('.'),
				StringComparison.OrdinalIgnoreCase)
			&& string.Equals(left.Path, right.Path, StringComparison.Ordinal);

		private static string BuildCookieMetadataKey(string name, string domain, string path) =>
			$"{name.ToUpperInvariant()}|{domain.TrimStart('.').ToUpperInvariant()}|{path}";

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

		private bool IsExpiredSessionResponse(HttpResponseMessage response,
			int authenticationGeneration) =>
			response.StatusCode == HttpStatusCode.Unauthorized
			|| (response.StatusCode == HttpStatusCode.Forbidden
				&& Volatile.Read(ref _authenticationGeneration) != authenticationGeneration)
			|| (IsRedirect(response.StatusCode)
				&& response.Headers.Location != null
				&& IsLoginUri(response.Headers.Location.IsAbsoluteUri
					? response.Headers.Location
					: new Uri(response.RequestMessage?.RequestUri ?? _appUri, response.Headers.Location)));

		private bool IsLoginUri(Uri uri) => IsTrustedOrigin(uri)
			&& uri.AbsolutePath.IndexOf("/Login/", StringComparison.OrdinalIgnoreCase) >= 0;

		private bool ShouldStopAtRedirect(Uri location, bool stopAtAuthenticationRedirect) =>
			(stopAtAuthenticationRedirect && IsLoginUri(location)) || !IsTrustedOrigin(location);

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

	internal sealed class CreatioSessionExpiredException : HttpRequestException
	{
		internal CreatioSessionExpiredException(int authenticationGeneration,
			HttpResponseMessage response)
			: base("The Creatio session expired and must be authenticated again.")
		{
			AuthenticationGeneration = authenticationGeneration;
			Response = response;
		}

		internal int AuthenticationGeneration { get; }

		internal HttpResponseMessage Response { get; }
	}

	internal sealed class CreatioBearerTokenExpiredException : HttpRequestException
	{
		internal CreatioBearerTokenExpiredException(int authenticationGeneration,
			HttpResponseMessage response)
			: base("The OAuth access token expired and must be refreshed.")
		{
			AuthenticationGeneration = authenticationGeneration;
			Response = response;
		}

		internal int AuthenticationGeneration { get; }

		internal HttpResponseMessage Response { get; }
	}
}
