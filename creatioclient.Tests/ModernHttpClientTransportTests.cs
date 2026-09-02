using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace Creatio.Client.Tests;

[TestFixture]
public class ModernHttpClientTransportTests
{
	private static ScriptedResponse OAuthTokenResponse(string accessToken) => new(
		Body: $"{{\"access_token\":\"{accessToken}\",\"expires_in\":3600,\"token_type\":\"Bearer\"}}");

	[Test]
	public async Task ExecuteGetRequestAsync_ShouldReturnRealResponseWithStatusHeadersAndContent()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(new ScriptedResponse(
			StatusCode: 500,
			Body: "response-body",
			Headers: new Dictionary<string, string[]> { ["X-Creatio-Test"] = new[] { "present" } }));
		using CreatioClient client = new(server.BaseUri.ToString(), "token");

		using HttpResponseMessage response = await client.ExecuteGetRequestAsync(server.BaseUri.ToString());
		CapturedRequest request = (await capture).Single();

		response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
		response.Headers.GetValues("X-Creatio-Test").Should().ContainSingle().Which.Should().Be("present");
		(await response.Content.ReadAsStringAsync()).Should().Be("response-body");
		request.Headers["Authorization"].Should().Be("Bearer token");
		request.Headers.Should().NotContainKey("BPMCSRF");
	}

	[TestCase("token")]
	[TestCase("Bearer token")]
	public async Task BearerConstructor_ShouldSendExactlyOneBearerPrefix(string token)
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(new ScriptedResponse(Body: "ok"));
		using CreatioClient client = new(server.BaseUri.ToString(), token);

		using HttpResponseMessage response = await client.ExecuteGetRequestAsync(server.BaseUri.ToString());
		CapturedRequest request = (await capture).Single();

		request.Headers["Authorization"].Should().Be("Bearer token");
		request.Headers.Should().NotContainKey("BPMCSRF");
	}

	[Test]
	public async Task BearerUnauthorized_ShouldReturnResponseWithoutAuthenticationReplay()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			new ScriptedResponse(StatusCode: 401));
		using CreatioClient client = new(server.BaseUri.ToString(), "token");

		using HttpResponseMessage response = await client.ExecuteGetRequestAsync(server.BaseUri.ToString());
		IReadOnlyList<CapturedRequest> requests = await capture;

		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		requests.Should().ContainSingle();
		requests[0].Headers["Authorization"].Should().Be("Bearer token");
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task OAuthClientCredentialsUnauthorized_ShouldRefreshAndReplayRequest(bool useFactory)
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			OAuthTokenResponse("token-one"),
			new ScriptedResponse(StatusCode: 401),
			OAuthTokenResponse("token-two"),
			new ScriptedResponse(Body: "recovered"));
		string authApp = new Uri(server.BaseUri, "connect/token").ToString();
		using CreatioClient client = useFactory
			? CreatioClient.CreateOAuth20Client(server.BaseUri.ToString(), authApp, "client", "secret")
			: new CreatioClient(server.BaseUri.ToString(), authApp, "client", "secret");

		using HttpResponseMessage response = await client.ExecutePostRequestAsync(
			new Uri(server.BaseUri, "data").ToString(), "{\"value\":42}");
		IReadOnlyList<CapturedRequest> requests = await capture;

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		(await response.Content.ReadAsStringAsync()).Should().Be("recovered");
		requests.Select(request => request.Target).Should().ContainInOrder(
			"/connect/token", "/data", "/connect/token", "/data");
		requests.Where(request => request.Target == "/connect/token")
			.Select(request => System.Text.Encoding.UTF8.GetString(request.Body))
			.Should().OnlyContain(body =>
				body == "client_id=client&client_secret=secret&grant_type=client_credentials");
		requests.Where(request => request.Target == "/data").Select(request => request.Headers["Authorization"])
			.Should().ContainInOrder("Bearer token-one", "Bearer token-two");
		requests.Where(request => request.Target == "/data").Select(request => request.Method)
			.Should().OnlyContain(method => method == "POST");
		requests.Where(request => request.Target == "/data")
			.Select(request => System.Text.Encoding.UTF8.GetString(request.Body))
			.Should().OnlyContain(body => body == "{\"value\":42}");
	}

	[Test]
	public async Task OAuthClientCredentialsUnauthorizedAfterRefresh_ShouldReturnSecondUnauthorized()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			OAuthTokenResponse("token-one"),
			new ScriptedResponse(StatusCode: 401),
			OAuthTokenResponse("token-two"),
			new ScriptedResponse(StatusCode: 401));
		string authApp = new Uri(server.BaseUri, "connect/token").ToString();
		using CreatioClient client = new(server.BaseUri.ToString(), authApp, "client", "secret");

		using HttpResponseMessage response = await client.ExecuteGetRequestAsync(
			new Uri(server.BaseUri, "data").ToString());
		IReadOnlyList<CapturedRequest> requests = await capture;

		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		requests.Count(request => request.Target == "/connect/token").Should().Be(2);
		requests.Count(request => request.Target == "/data").Should().Be(2);
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task OAuthTokenFailure_ShouldPreserveConstructionFailureShape(bool useFactory)
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			new ScriptedResponse(StatusCode: 500));
		string authApp = new Uri(server.BaseUri, "connect/token").ToString();

		Action act = () => {
			using CreatioClient client = useFactory
				? CreatioClient.CreateOAuth20Client(server.BaseUri.ToString(), authApp, "client", "secret")
				: new CreatioClient(server.BaseUri.ToString(), authApp, "client", "secret");
		};

		if (useFactory) {
			act.Should().Throw<AggregateException>()
				.Which.InnerException.Should().BeOfType<HttpRequestException>();
		} else {
			act.Should().Throw<HttpRequestException>().Which.Should().NotBeOfType<AggregateException>();
		}
		(await capture).Should().ContainSingle();
	}

	[Test]
	public async Task ExecuteGetRequestAsync_ShouldHonorCallerCancellationWithoutRetrying()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			new ScriptedResponse(Body: "too-late", Delay: TimeSpan.FromMilliseconds(300)));
		using CreatioClient client = new(server.BaseUri.ToString(), "token");
		using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(50));

		Func<Task> act = async () => {
			using HttpResponseMessage response = await client.ExecuteGetRequestAsync(server.BaseUri.ToString(),
				maxAttempts: 3, delaySec: 0, cancellationToken: cancellation.Token);
		};

		await act.Should().ThrowAsync<OperationCanceledException>();
		(await capture).Should().ContainSingle();
	}

	[Test]
	public async Task AuthenticationHandler_ShouldReturnCancellationWhenInnerHandlerFinishesLate()
	{
		Uri appUri = new("https://creatio.test/");
		using CreatioAuthenticationHandler authentication = new(appUri, new CookieContainer(), null, null,
			null, () => "token", () => null, () => true, true,
			new DelayedIgnoringCancellationHandler());
		using HttpMessageInvoker invoker = new(authentication);
		using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(20));

		Func<Task> act = async () => {
			using HttpResponseMessage response = await invoker.SendAsync(
				new HttpRequestMessage(HttpMethod.Get, appUri), cancellation.Token);
		};

		await act.Should().ThrowAsync<OperationCanceledException>();
		await Task.Delay(150);
	}

	[Test]
	public async Task ConcurrentCookieRequests_ShouldAuthenticateOnceAndShareTheSession()
	{
		const int requestCount = 5;
		await using ScriptedLoopbackHttpServer server = new();
		ScriptedResponse[] responses = new[] { LoginResponse() }
			.Concat(Enumerable.Range(0, requestCount).Select(index => new ScriptedResponse(Body: $"ok-{index}")))
			.ToArray();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(responses);
		using CreatioClient client = new(server.BaseUri.ToString(), "user", "password") { SkipPing = true };

		Task<HttpResponseMessage>[] calls = Enumerable.Range(0, requestCount)
			.Select(index => client.ExecuteGetRequestAsync(new Uri(server.BaseUri, $"data/{index}").ToString()))
			.ToArray();
		HttpResponseMessage[] results = await Task.WhenAll(calls);
		try {
			IReadOnlyList<CapturedRequest> requests = await capture;
			requests.Count(request => request.Target == "/ServiceModel/AuthService.svc/Login").Should().Be(1);
			requests.Where(request => request.Method == "GET").Should().HaveCount(requestCount)
				.And.OnlyContain(request => request.Headers["BPMCSRF"] == "csrf-token");
		} finally {
			foreach (HttpResponseMessage response in results) {
				response.Dispose();
			}
		}
	}

	[TestCase("POST")]
	[TestCase("PUT")]
	[TestCase("PATCH")]
	[TestCase("DELETE")]
	[Description("Every cookie-authenticated mutable request echoes a modern CRT_CSRF token under its issued name.")]
	public async Task AuthenticationHandler_ShouldSendModernCsrfHeader_WhenMutableRequestUsesCookieSession(
		string method)
	{
		// Arrange
		Uri appUri = new("https://creatio.test/");
		CookieContainer cookies = new();
		cookies.Add(appUri, new Cookie(".ASPXAUTH", "session-token"));
		cookies.Add(appUri, new Cookie("CRT_CSRF", "modern-token"));
		SequenceHandler inner = new(_ => Response(HttpStatusCode.OK, "ok"));
		using CreatioAuthenticationHandler authentication = new(appUri, cookies, "user", "password", null,
			() => null, () => null, () => true, true, inner);
		using HttpMessageInvoker invoker = new(authentication);

		// Act
		using HttpResponseMessage response = await invoker.SendAsync(
			new HttpRequestMessage(new HttpMethod(method), appUri), CancellationToken.None);

		// Assert
		inner.Requests.Should().ContainSingle(because: "one mutable request was sent");
		inner.Requests[0].Headers.GetValues("CRT_CSRF").Should().ContainSingle().Which.Should()
			.Be("modern-token", because: "the token must be echoed under the modern cookie name");
		inner.Requests[0].Headers.Should().NotContain(header => header.Key == "BPMCSRF",
			because: "the legacy name must not be invented for a modern session");
	}

	[Test]
	[Description("The modern token wins when a transition environment issues both CSRF cookie names.")]
	public async Task AuthenticationHandler_ShouldPreferModernCsrfHeader_WhenBothCookiesExist()
	{
		// Arrange
		Uri appUri = new("https://creatio.test/");
		CookieContainer cookies = new();
		cookies.Add(appUri, new Cookie(".ASPXAUTH", "session-token"));
		cookies.Add(appUri, new Cookie("BPMCSRF", "legacy-token"));
		cookies.Add(appUri, new Cookie("CRT_CSRF", "modern-token"));
		SequenceHandler inner = new(_ => Response(HttpStatusCode.OK, "ok"));
		using CreatioAuthenticationHandler authentication = new(appUri, cookies, "user", "password", null,
			() => null, () => null, () => true, true, inner);
		using HttpMessageInvoker invoker = new(authentication);

		// Act
		using HttpResponseMessage response = await invoker.SendAsync(
			new HttpRequestMessage(HttpMethod.Post, appUri), CancellationToken.None);

		// Assert
		inner.Requests[0].Headers.GetValues("CRT_CSRF").Should().ContainSingle().Which.Should()
			.Be("modern-token", because: "current Creatio runtimes issue CRT_CSRF");
		inner.Requests[0].Headers.Should().NotContain(header => header.Key == "BPMCSRF",
			because: "only the selected cookie name may be echoed");
	}

	[Test]
	[Description("A tokenless cookie session reaches the server without a fabricated CSRF header.")]
	public async Task AuthenticationHandler_ShouldOmitCsrfHeader_WhenSessionHasNoCsrfCookie()
	{
		// Arrange
		Uri appUri = new("https://creatio.test/");
		CookieContainer cookies = new();
		cookies.Add(appUri, new Cookie(".ASPXAUTH", "session-token"));
		SequenceHandler inner = new(_ => Response(HttpStatusCode.OK, "ok"));
		using CreatioAuthenticationHandler authentication = new(appUri, cookies, "user", "password", null,
			() => null, () => null, () => true, true, inner);
		using HttpMessageInvoker invoker = new(authentication);

		// Act
		using HttpResponseMessage response = await invoker.SendAsync(
			new HttpRequestMessage(HttpMethod.Post, appUri), CancellationToken.None);

		// Assert
		inner.Requests[0].Headers.Should().NotContain(header =>
			header.Key == "CRT_CSRF" || header.Key == "BPMCSRF",
			because: "the server is authoritative when CSRF validation is disabled");
	}

	[Test]
	[Description("Image API upload uses authenticated transport and the exact browser-compatible binary headers.")]
	public async Task UploadImageAsync_ShouldSendBinaryPayloadAndImageApiHeaders()
	{
		// Arrange
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(new ScriptedResponse(Body: "{}"));
		using CreatioClient client = new(server.BaseUri.ToString(), "token");
		byte[] payload = { 1, 2, 3, 4 };

		// Act
		using HttpResponseMessage response = await client.UploadImageAsync(
			new Uri(server.BaseUri, "ImageAPIService/upload?fileId=1").ToString(), payload,
			"brand logo.png", "image/png");
		CapturedRequest request = (await capture).Single();

		// Assert
		request.Method.Should().Be("POST", because: "the Image API accepts a single POST");
		request.Body.Should().Equal(payload, because: "the image bytes must not be transformed");
		request.Headers["Content-Type"].Should().Be("image/png", because: "the server validates the MIME type");
		request.Headers["Content-Range"].Should().Be("bytes 0-3/4",
			because: "the range end is inclusive and zero based");
		request.Headers["Content-Disposition"].Should().Be("attachment; filename=brand%20logo.png",
			because: "Creatio rejects quoted or filename-star forms for this endpoint");
	}

	[Test]
	[Description("Session cookies can be imported for reuse and exported as detached copies for browser storage.")]
	public async Task SessionCookies_ShouldRoundTripWithoutSharingMutableCookieInstances()
	{
		// Arrange
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(new ScriptedResponse(Body: "ok"));
		using CreatioClient client = new(server.BaseUri.ToString(), "user", "password") { SkipPing = true };
		CreatioSessionCookie authCookie = new(".ASPXAUTH", "session-token", string.Empty, "/",
			httpOnly: true, secure: false, sameSite: "Strict", DateTime.MinValue);
		CreatioSessionCookie csrfCookie = new("CRT_CSRF", "csrf-token", server.BaseUri.Host, "/",
			httpOnly: false, secure: false, sameSite: "None", DateTime.MinValue);
		CreatioSessionCookie normalizedCookie = new("custom", "value", server.BaseUri.Host, "/",
			httpOnly: false, secure: false, sameSite: "unexpected", DateTime.MinValue);
		CreatioSessionCookie nullPolicyCookie = new("custom-null", "value", server.BaseUri.Host, "/",
			httpOnly: false, secure: false, sameSite: null, DateTime.MinValue);
		CreatioSessionCookie rootIdentityCookie = new("SID", "root", server.BaseUri.Host, "/",
			httpOnly: false, secure: false, sameSite: "Lax", DateTime.MinValue);
		CreatioSessionCookie nestedIdentityCookie = new("SID", "nested", server.BaseUri.Host, "/nested",
			httpOnly: false, secure: false, sameSite: "Strict", DateTime.MinValue);
		client.ImportSessionCookies(new[] { authCookie, csrfCookie, normalizedCookie, nullPolicyCookie,
			rootIdentityCookie, nestedIdentityCookie });

		// Act
		using HttpResponseMessage response = await client.ExecuteGetRequestAsync(server.BaseUri.ToString());
		IReadOnlyList<CreatioSessionCookie> exported = client.ExportSessionCookies();
		CapturedRequest request = (await capture).Single();

		// Assert
		request.Headers["Cookie"].Should().Contain(".ASPXAUTH=session-token",
			because: "the imported session is reused without another login");
		request.Headers["CRT_CSRF"].Should().Be("csrf-token",
			because: "the imported modern token is applied to the request");
		exported.Should().Contain(cookie => cookie.Name == ".ASPXAUTH" && cookie.Value == "session-token",
			because: "exported cookies are detached from the caller's mutable input");
		exported.Single(cookie => cookie.Name == ".ASPXAUTH").SameSite.Should().Be("Strict",
			because: "browser storage must retain the server's cookie policy");
		exported.Single(cookie => cookie.Name == "CRT_CSRF").SameSite.Should().Be("None",
			because: "an explicit cross-site cookie policy must not be downgraded to Lax");
		exported.Where(cookie => cookie.Name.StartsWith("custom", StringComparison.Ordinal))
			.Should().OnlyContain(cookie => cookie.SameSite == "Lax",
				because: "unknown or absent browser policies must normalize to the safe default");
		exported.Single(cookie => cookie.Name == "SID").SameSite.Should().Be("Lax",
			because: "a same-name cookie on another path has a distinct browser policy");
	}

	[Test]
	[Description("Session export preserves SameSite attributes received from Creatio login.")]
	public async Task ExportSessionCookies_ShouldPreserveSameSite_FromLoginResponse()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(new ScriptedResponse(
			Body: "{\"Code\":0}",
			Headers: new Dictionary<string, string[]> {
				["Set-Cookie"] = new[] {
					".ASPXAUTH=session-token; Path=/; HttpOnly; SameSite=Strict",
					"CRT_CSRF=csrf-token; Path=/; SameSite=None",
					"BPMCSRF=legacy-token; Path=/"
				}
			}));
		using CreatioClient client = new(server.BaseUri.ToString(), "user", "password") { SkipPing = true };

		using HttpResponseMessage response = await client.LoginAsync();
		IReadOnlyList<CreatioSessionCookie> exported = client.ExportSessionCookies();
		await capture;

		exported.Single(cookie => cookie.Name == ".ASPXAUTH").SameSite.Should().Be("Strict",
			because: "strict browser-session policy must survive the HTTP cookie jar");
		exported.Single(cookie => cookie.Name == "CRT_CSRF").SameSite.Should().Be("None",
			because: "cross-site browser-session policy must survive the HTTP cookie jar");
		exported.Single(cookie => cookie.Name == "BPMCSRF").SameSite.Should().Be("Lax",
			because: "a cookie without SameSite metadata uses the safe browser default");
	}

	[Test]
	[Description("A replacement cookie without SameSite resets stale metadata to the safe default.")]
	public async Task ExportSessionCookies_ShouldResetSameSite_WhenReplacementOmitsAttribute()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			new ScriptedResponse(Body: "{\"Code\":0}", Headers: new Dictionary<string, string[]> {
				["Set-Cookie"] = new[] { ".ASPXAUTH=first; Path=/; SameSite=None" }
			}),
			new ScriptedResponse(Body: "{\"Code\":0}", Headers: new Dictionary<string, string[]> {
				["Set-Cookie"] = new[] { ".ASPXAUTH=second; Path=/" }
			}));
		using CreatioClient client = new(server.BaseUri.ToString(), "user", "password") { SkipPing = true };

		using (HttpResponseMessage first = await client.LoginAsync()) { }
		using (HttpResponseMessage second = await client.LoginAsync()) { }
		IReadOnlyList<CreatioSessionCookie> exported = client.ExportSessionCookies();
		await capture;

		exported.Single(cookie => cookie.Name == ".ASPXAUTH").SameSite.Should().Be("Lax",
			because: "the current server response omitted SameSite and must not inherit a prior value");
	}

	[Test]
	[Description("Session export defaults cookies without captured browser metadata to SameSite Lax.")]
	public void ExportSessionCookies_ShouldUseLax_WhenCookieHasNoMetadata()
	{
		Uri appUri = new("https://creatio.test/");
		using CreatioClient client = new(appUri.ToString(), "token");
		client.AuthCookie.Add(appUri, new Cookie("manual", "value"));

		IReadOnlyList<CreatioSessionCookie> exported = client.ExportSessionCookies();

		exported.Single(cookie => cookie.Name == "manual").SameSite.Should().Be("Lax",
			because: "a cookie not received or imported through the metadata bridge uses the safe default");
	}

	[Test]
	[Description("Cookie metadata ignores invalid domains and headers not accepted by the shared cookie jar.")]
	public async Task AuthenticationHandler_ShouldIgnoreMetadata_WhenSetCookieIsNotAccepted()
	{
		Uri appUri = new("https://creatio.test/");
		CookieContainer cookies = new();
		cookies.Add(appUri, new Cookie("existing", "value"));
		SequenceHandler inner = new(request => {
			HttpResponseMessage response = Response(HttpStatusCode.OK, "ok");
			response.RequestMessage = request;
			response.Headers.TryAddWithoutValidation("Set-Cookie", new[] {
				"foreign=value; Domain=other.invalid; Path=/",
				"not-stored=value; Path=/; SameSite=Strict"
			});
			return response;
		});
		using CreatioAuthenticationHandler authentication = new(appUri, cookies, null, null, null,
			() => "token", () => null, () => true, true, inner);
		using HttpMessageInvoker invoker = new(authentication);

		using HttpResponseMessage response = await invoker.SendAsync(
			new HttpRequestMessage(HttpMethod.Get, appUri), CancellationToken.None);

		response.StatusCode.Should().Be(HttpStatusCode.OK,
			because: "unaccepted metadata must not disrupt the authenticated response");
	}

	[Test]
	public async Task DisposedClient_ShouldRejectNewRequests()
	{
		CreatioClient client = new("https://example.invalid", "token");
		client.Dispose();

		Func<Task> act = async () => {
			using HttpResponseMessage response = await client.ExecuteGetRequestAsync("https://example.invalid/data");
		};

		await act.Should().ThrowAsync<ObjectDisposedException>();
	}

	[Test]
	public async Task Dispose_ShouldCancelAnActiveRequestAndRemainIdempotent()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			new ScriptedResponse(Body: "late", Delay: TimeSpan.FromMilliseconds(500)));
		CreatioClient client = new(server.BaseUri.ToString(), "token");
		Task<HttpResponseMessage> request = client.ExecuteGetRequestAsync(server.BaseUri.ToString());
		await Task.Delay(50);

		client.Dispose();
		client.Dispose();

		await request.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
		await capture;
	}

	[Test]
	public async Task LoginAsync_ShouldRejectCreatioCodeOneResponse()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			new ScriptedResponse(Body: "{\"Code\":1}"));
		using CreatioClient client = new(server.BaseUri.ToString(), "user", "bad-password");

		Func<Task> act = async () => {
			using HttpResponseMessage response = await client.LoginAsync();
		};

		await act.Should().ThrowAsync<UnauthorizedAccessException>();
		await capture;
	}

	[TestCase(2)]
	[TestCase(8)]
	[Description("LoginAsync rejects every nonzero Creatio authentication result code.")]
	public async Task LoginAsync_ShouldRejectEveryNonzeroCreatioCode(int code)
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			new ScriptedResponse(Body: $"{{\"Code\":{code}}}"));
		using CreatioClient client = new(server.BaseUri.ToString(), "user", "password");

		Func<Task> act = async () => {
			using HttpResponseMessage response = await client.LoginAsync();
		};

		await act.Should().ThrowAsync<UnauthorizedAccessException>(
			because: "only Code zero represents an authenticated Creatio session");
		await capture;
	}

	[TestCase("{}")]
	[TestCase("not-json")]
	[TestCase("{\"Code\":9223372036854775808}")]
	[Description("LoginAsync rejects malformed authentication result envelopes.")]
	public async Task LoginAsync_ShouldRejectMalformedCreatioCode(string body)
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(new ScriptedResponse(Body: body));
		using CreatioClient client = new(server.BaseUri.ToString(), "user", "password");

		Func<Task> act = async () => {
			using HttpResponseMessage response = await client.LoginAsync();
		};

		await act.Should().ThrowAsync<UnauthorizedAccessException>(
			because: "a response without an explicit Code zero cannot establish a session");
		await capture;
	}

	[Test]
	[Description("A rejected login cannot leave response cookies usable by a later request.")]
	public async Task LoginAsync_ShouldDiscardCookies_WhenCreatioCodeRejectsAuthentication()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			new ScriptedResponse(Body: "{\"Code\":2}", Headers: new Dictionary<string, string[]> {
				["Set-Cookie"] = new[] { ".ASPXAUTH=rejected; Path=/data", "CRT_CSRF=rejected-csrf; Path=/data" }
			}),
			LoginResponse("accepted", "accepted-csrf"),
			new ScriptedResponse(Body: "data"));
		using CreatioClient client = new(server.BaseUri.ToString(), "user", "password") { SkipPing = true };

		Func<Task> rejectedLogin = async () => {
			using HttpResponseMessage response = await client.LoginAsync();
		};
		await rejectedLogin.Should().ThrowAsync<UnauthorizedAccessException>();
		using HttpResponseMessage data = await client.ExecuteGetRequestAsync(new Uri(server.BaseUri, "data").ToString());
		IReadOnlyList<CapturedRequest> requests = await capture;

		requests.Count(request => request.Target.EndsWith("/ServiceModel/AuthService.svc/Login",
			StringComparison.Ordinal)).Should().Be(2,
			because: "the rejected response cookie cannot satisfy the next authentication check");
		requests.Last().Headers["Cookie"].Should().Contain(".ASPXAUTH=accepted")
			.And.NotContain(".ASPXAUTH=rejected",
				because: "only the successfully authenticated session may reach application data");
	}

	[Test]
	[Description("An HTTP authentication failure cannot leave response cookies usable by a later request.")]
	public async Task LoginAsync_ShouldDiscardCookies_WhenHttpStatusRejectsAuthentication()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			new ScriptedResponse(StatusCode: 401, Body: "rejected", Headers: new Dictionary<string, string[]> {
				["Set-Cookie"] = new[] { ".ASPXAUTH=rejected; Path=/", "CRT_CSRF=rejected-csrf; Path=/" }
			}),
			LoginResponse("accepted", "accepted-csrf"),
			new ScriptedResponse(Body: "data"));
		using CreatioClient client = new(server.BaseUri.ToString(), "user", "password") { SkipPing = true };

		using (HttpResponseMessage rejected = await client.LoginAsync()) {
			rejected.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
				because: "protocol-error response compatibility remains intact");
		}
		using HttpResponseMessage data = await client.ExecuteGetRequestAsync(new Uri(server.BaseUri, "data").ToString());
		IReadOnlyList<CapturedRequest> requests = await capture;

		requests.Count(request => request.Target.EndsWith("/ServiceModel/AuthService.svc/Login",
			StringComparison.Ordinal)).Should().Be(2,
			because: "a non-success response cookie cannot satisfy the next authentication check");
		requests.Last().Headers["Cookie"].Should().Contain(".ASPXAUTH=accepted")
			.And.NotContain(".ASPXAUTH=rejected",
				because: "only a successful login may establish the application session");
	}

	[Test]
	[Description("Failed-login cleanup safely ignores malformed, unrelated, and pathless Set-Cookie headers.")]
	public async Task LoginAsync_ShouldIgnoreNonSessionCookieHeaders_WhenAuthenticationFails()
	{
		Uri appUri = new("https://creatio.test/");
		SequenceHandler inner = new(request => {
			HttpResponseMessage response = Response(HttpStatusCode.Unauthorized, "rejected");
			response.RequestMessage = request;
			response.Headers.TryAddWithoutValidation("Set-Cookie", new[] {
				"malformed",
				"unrelated=value; Path=/data",
				".ASPXAUTH=value"
			});
			return response;
		});
		using CreatioAuthenticationHandler authentication = new(appUri, new CookieContainer(), "user",
			"password", null, () => null, () => null, () => true, true, inner);

		using HttpResponseMessage response = await authentication.LoginAsync(1000, CancellationToken.None);

		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
			because: "irrelevant response-cookie syntax must not replace the protocol error response");
	}

	[Test]
	public async Task LoginAsync_ShouldApplyTimeoutWhileReadingResponseBody()
	{
		await using ScriptedLoopbackHttpServer server = new();
		_ = server.CaptureAsync(
			LoginResponse() with { BodyDelay = TimeSpan.FromMilliseconds(400) });
		using CreatioClient client = new(server.BaseUri.ToString(), "user", "password");

		Func<Task> act = async () => {
			using HttpResponseMessage response = await client.LoginAsync(requestTimeout: 50);
		};

		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	[Test]
	[Description("A login body-read failure invalidates every session cookie received in its headers.")]
	public async Task LoginAsync_ShouldDiscardCookies_WhenBodyValidationFails()
	{
		Uri appUri = new("https://creatio.test/");
		CookieContainer cookies = new();
		SequenceHandler inner = new(
			request => {
				cookies.Add(appUri, new Cookie(".ASPXAUTH", "unvalidated", "/data", appUri.Host));
				cookies.Add(appUri, new Cookie("CRT_CSRF", "unvalidated-csrf", "/data", appUri.Host));
				HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ThrowingContent() };
				response.Headers.TryAddWithoutValidation("Set-Cookie", new[] {
					".ASPXAUTH=unvalidated; Path=/data", "CRT_CSRF=unvalidated-csrf; Path=/data"
				});
				return response;
			},
			request => {
				cookies.Add(appUri, new Cookie(".ASPXAUTH", "accepted", "/", appUri.Host));
				cookies.Add(appUri, new Cookie("CRT_CSRF", "accepted-csrf", "/", appUri.Host));
				return Response(HttpStatusCode.OK, "{\"Code\":0}");
			},
			request => Response(HttpStatusCode.OK, "data"));
		using CreatioAuthenticationHandler authentication = new(appUri, cookies, "user", "password", null,
			() => null, () => null, () => true, true, inner);
		using HttpMessageInvoker invoker = new(authentication);

		Func<Task> rejectedLogin = async () => {
			using HttpResponseMessage response = await authentication.LoginAsync(1000, CancellationToken.None);
		};
		await rejectedLogin.Should().ThrowAsync<HttpRequestException>();
		using HttpResponseMessage data = await invoker.SendAsync(
			new HttpRequestMessage(HttpMethod.Get, new Uri(appUri, "data")), CancellationToken.None);

		cookies.GetCookies(new Uri(appUri, "data")).Cast<Cookie>().Select(cookie => cookie.Value)
			.Should().Contain("accepted").And.NotContain("unvalidated",
				because: "headers from a login whose body was never validated cannot establish a session");
	}

	[Test]
	public async Task LoginAsync_ShouldReturnNonSuccessResponseWithoutMarkingSessionAuthenticated()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			new ScriptedResponse(StatusCode: 500, Body: "login-error"));
		using CreatioClient client = new(server.BaseUri.ToString(), "user", "password");

		using HttpResponseMessage response = await client.LoginAsync();
		await capture;

		response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
	}

	[Test]
	public async Task SuccessfulLoginWithoutAuthCookie_ShouldNotMarkPasswordSessionAuthenticated()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			new ScriptedResponse(Body: "{\"Code\":0}"),
			LoginResponse(),
			new ScriptedResponse(Body: "data"));
		using CreatioClient client = new(server.BaseUri.ToString(), "user", "password") { SkipPing = true };

		using (HttpResponseMessage login = await client.LoginAsync()) {
			login.StatusCode.Should().Be(HttpStatusCode.OK);
		}
		using HttpResponseMessage response = await client.ExecuteGetRequestAsync(
			new Uri(server.BaseUri, "data").ToString());
		IReadOnlyList<CapturedRequest> requests = await capture;

		requests.Count(item => item.Target.EndsWith("/ServiceModel/AuthService.svc/Login")).Should().Be(2);
	}

	[Test]
	public async Task LazyPasswordAuthentication_ShouldRejectSuccessWithoutAuthCookie()
	{
		Uri appUri = new("https://creatio.test/");
		SequenceHandler inner = new(request => Response(HttpStatusCode.OK, "{\"Code\":0}"));
		using CreatioAuthenticationHandler authentication = new(appUri, new CookieContainer(), "user",
			"password", null, () => null, () => 0, () => true, true, inner);
		using HttpMessageInvoker invoker = new(authentication);

		Func<Task> act = async () => {
			using HttpResponseMessage response = await invoker.SendAsync(
				new HttpRequestMessage(HttpMethod.Get, new Uri(appUri, "data")), CancellationToken.None);
		};

		await act.Should().ThrowAsync<UnauthorizedAccessException>();
		inner.Requests.Should().ContainSingle();
	}

	[Test]
	public async Task LoginAsync_ShouldUseNtlmLoginRoute_WhenWindowsCredentialsAreConfigured()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(LoginResponse());
		using CreatioClient client = new(server.BaseUri.ToString(), true,
			new NetworkCredential("windows-user", "windows-password"));

		using HttpResponseMessage response = await client.LoginAsync();
		CapturedRequest request = (await capture).Single();

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		request.Method.Should().Be("GET");
		request.Target.Should().Be("/Login/NuiLogin.aspx?ntlmlogin");
	}

	[Test]
	public async Task NtlmLoginAsync_ShouldDisposeResponse_WhenBodyReadFails()
	{
		Uri appUri = new("https://creatio.test/");
		ThrowingContent content = new();
		SequenceHandler inner = new(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) {
			Content = content
		});
		using CreatioAuthenticationHandler authentication = new(appUri, new CookieContainer(), null,
			null, new NetworkCredential("windows-user", "windows-password"), () => null, () => 0,
			() => true, true, inner);

		Func<Task> act = async () => {
			using HttpResponseMessage response = await authentication.LoginAsync(1000, CancellationToken.None);
		};

		await act.Should().ThrowAsync<HttpRequestException>();
		content.IsDisposed.Should().BeTrue();
	}

	[Test]
	public async Task LazyAuthenticationError_ShouldCloneResponseWithoutRequestOrBody()
	{
		Uri appUri = new("https://creatio.test/");
		SequenceHandler inner = new(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
		using CreatioAuthenticationHandler authentication = new(appUri, new CookieContainer(), "user",
			"password", null, () => null, () => 0, () => true, true, inner);

		Func<Task> act = () => authentication.EnsureAuthenticatedForRequestAsync(CancellationToken.None);

		CreatioAuthenticationHttpException exception = (await act.Should()
			.ThrowAsync<CreatioAuthenticationHttpException>()).Which;
		using (exception.Response) {
			exception.Response.RequestMessage.Should().BeNull();
			exception.Response.Content.Should().NotBeNull();
			(await exception.Response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
		}
	}

	[Test]
	public async Task LazyPasswordAuthentication_ShouldPingBeforeRequest_ForLegacyNetFrameworkMode()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			LoginResponse(),
			new ScriptedResponse(Body: "pong"),
			new ScriptedResponse(Body: "data"));
		using CreatioClient client = new(server.BaseUri.ToString(), "user", "password", isNetCore: false);

		using HttpResponseMessage response = await client.ExecuteGetRequestAsync(
			new Uri(server.BaseUri, "data").ToString());
		IReadOnlyList<CapturedRequest> requests = await capture;

		requests.Select(request => request.Target).Should().ContainInOrder(
			"/ServiceModel/AuthService.svc/Login", "/0/ping", "/data");
	}

	[Test]
	public async Task AuthenticationHandler_ShouldTreatPingTransportFailureAsNonFatal()
	{
		Uri appUri = new("https://creatio.test/");
		CookieContainer cookies = new();
		SequenceHandler inner = new(
			request => {
				cookies.Add(appUri, new Cookie(".ASPXAUTH", "session-token"));
				cookies.Add(appUri, new Cookie("BPMCSRF", "csrf-token"));
				return Response(HttpStatusCode.OK, "{\"Code\":0}");
			},
			request => throw new HttpRequestException("transient ping failure"),
			request => Response(HttpStatusCode.OK, "data"));
		using CreatioAuthenticationHandler authentication = new(appUri, cookies, "user", "password", null,
			() => null, () => 0, () => false, false, inner);
		using HttpMessageInvoker invoker = new(authentication);

		using HttpResponseMessage response = await invoker.SendAsync(
			new HttpRequestMessage(HttpMethod.Get, new Uri(appUri, "data")), CancellationToken.None);

		inner.Requests.Select(request => request.RequestUri!.AbsolutePath).Should().ContainInOrder(
			"/ServiceModel/AuthService.svc/Login", "/0/ping", "/data");
	}

	[Test]
	public async Task AuthenticationHandler_ShouldUseNtlmPrimitiveDuringLazyAuthentication()
	{
		Uri appUri = new("https://creatio.test/");
		CookieContainer cookies = new();
		SequenceHandler inner = new(
			request => Response(HttpStatusCode.OK, string.Empty),
			request => Response(HttpStatusCode.OK, "data"));
		using CreatioAuthenticationHandler authentication = new(appUri, cookies, null, null,
			new NetworkCredential("user", "password"), () => null, () => null, () => true, true, inner);
		using HttpMessageInvoker invoker = new(authentication);

		using HttpResponseMessage response = await invoker.SendAsync(
			new HttpRequestMessage(HttpMethod.Get, new Uri(appUri, "data")), CancellationToken.None);

		inner.Requests.Select(request => request.RequestUri!.PathAndQuery).Should().ContainInOrder(
			"/Login/NuiLogin.aspx?ntlmlogin", "/data");
	}

	[Test]
	public async Task AuthenticationHandler_ShouldPreserveVirtualDirectoryInAuthAndPingPaths()
	{
		Uri appUri = new("https://creatio.test/creatio/");
		CookieContainer cookies = new();
		SequenceHandler inner = new(
			request => {
				cookies.Add(appUri, new Cookie(".ASPXAUTH", "session-token"));
				cookies.Add(appUri, new Cookie("BPMCSRF", "csrf-token"));
				return Response(HttpStatusCode.OK, "{\"Code\":0}");
			},
			request => Response(HttpStatusCode.OK, "pong"),
			request => Response(HttpStatusCode.OK, "data"));
		using CreatioAuthenticationHandler authentication = new(appUri, cookies, "user", "password", null,
			() => null, () => 0, () => false, false, inner);
		using HttpMessageInvoker invoker = new(authentication);

		using HttpResponseMessage response = await invoker.SendAsync(
			new HttpRequestMessage(HttpMethod.Get, new Uri(appUri, "data")), CancellationToken.None);

		inner.Requests.Select(request => request.RequestUri!.AbsolutePath).Should().ContainInOrder(
			"/creatio/ServiceModel/AuthService.svc/Login", "/creatio/0/ping", "/creatio/data");
	}

	[Test]
	public async Task AuthenticationHandler_ShouldRejectCrossOriginRequestsBeforeAddingCredentials()
	{
		Uri appUri = new("https://creatio.test/");
		SequenceHandler inner = new(request => Response(HttpStatusCode.OK, "unexpected"));
		using CreatioAuthenticationHandler authentication = new(appUri, new CookieContainer(), null, null,
			null, () => "secret-token", () => null, () => true, true, inner);
		using HttpMessageInvoker invoker = new(authentication);

		Func<Task> act = async () => {
			using HttpResponseMessage response = await invoker.SendAsync(
				new HttpRequestMessage(HttpMethod.Get, "https://other.test/data"), CancellationToken.None);
		};

		await act.Should().ThrowAsync<InvalidOperationException>();
		inner.Requests.Should().BeEmpty();
	}

	[Test]
	public async Task CrossOriginRedirect_ShouldNotForwardCsrfHeader()
	{
		await using ScriptedLoopbackHttpServer destination = new();
		await using ScriptedLoopbackHttpServer source = new();
		Task<IReadOnlyList<CapturedRequest>> capture = source.CaptureAsync(
			LoginResponse(),
			new ScriptedResponse(StatusCode: 302, Headers: new Dictionary<string, string[]> {
				["Location"] = new[] { destination.BaseUri.ToString() }
			}));
		using CreatioClient client = new(source.BaseUri.ToString(), "user", "password") { SkipPing = true };

		using HttpResponseMessage response = await client.ExecuteGetRequestAsync(source.BaseUri.ToString());
		await capture;
		await Task.Delay(100);

		response.StatusCode.Should().Be(HttpStatusCode.Redirect);
		destination.HasPendingConnection.Should().BeFalse();
	}

	[Test]
	public async Task SameOriginRedirect_ShouldRemainTransparent()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			new ScriptedResponse(StatusCode: 302, Headers: new Dictionary<string, string[]> {
				["Location"] = new[] { "/final" }
			}),
			new ScriptedResponse(Body: "final"));
		using CreatioClient client = new(server.BaseUri.ToString(), "token");

		using HttpResponseMessage response = await client.ExecuteGetRequestAsync(server.BaseUri.ToString());
		IReadOnlyList<CapturedRequest> requests = await capture;

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		(await response.Content.ReadAsStringAsync()).Should().Be("final");
		requests.Select(item => item.Target).Should().ContainInOrder("/", "/final");
	}

	[Test]
	public async Task ExpiredCookieSessionRedirect_ShouldReloginAndReplayPostRequest()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			LoginResponse("session-one", "csrf-one"),
			new ScriptedResponse(Body: "first"),
			new ScriptedResponse(StatusCode: 302, Headers: new Dictionary<string, string[]> {
				["Location"] = new[] { "/Login/Login.html?ReturnUrl=%2Fdata" }
			}),
			LoginResponse("session-two", "csrf-two"),
			new ScriptedResponse(Body: "recovered"));
		using CreatioClient client = new(server.BaseUri.ToString(), "user", "password") { SkipPing = true };

		using (HttpResponseMessage first = await client.ExecuteGetRequestAsync(
			new Uri(server.BaseUri, "first").ToString())) { }
		using HttpResponseMessage recovered = await client.ExecutePostRequestAsync(
			new Uri(server.BaseUri, "data").ToString(), "{\"value\":42}");
		IReadOnlyList<CapturedRequest> requests = await capture;

		recovered.StatusCode.Should().Be(HttpStatusCode.OK);
		(await recovered.Content.ReadAsStringAsync()).Should().Be("recovered");
		requests.Select(item => item.Target).Should().ContainInOrder(
			"/ServiceModel/AuthService.svc/Login", "/first", "/data",
			"/ServiceModel/AuthService.svc/Login", "/data");
		requests.Last().Method.Should().Be("POST");
		System.Text.Encoding.UTF8.GetString(requests.Last().Body).Should().Be("{\"value\":42}");
		requests.Last().Headers["Cookie"].Should().Contain(".ASPXAUTH=session-two")
			.And.NotContain(".ASPXAUTH=session-one");
		requests.Last().Headers["BPMCSRF"].Should().Be("csrf-two");
	}

	[Test]
	public async Task ExpiredCookieSessionUnauthorized_ShouldReloginOnce()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			LoginResponse("session-one", "csrf-one"),
			new ScriptedResponse(Body: "first"),
			new ScriptedResponse(StatusCode: 401),
			LoginResponse("session-two", "csrf-two"),
			new ScriptedResponse(Body: "recovered"));
		using CreatioClient client = new(server.BaseUri.ToString(), "user", "password") { SkipPing = true };

		using (HttpResponseMessage first = await client.ExecuteGetRequestAsync(
			new Uri(server.BaseUri, "first").ToString())) { }
		using HttpResponseMessage recovered = await client.ExecuteGetRequestAsync(
			new Uri(server.BaseUri, "data").ToString());
		IReadOnlyList<CapturedRequest> requests = await capture;

		recovered.StatusCode.Should().Be(HttpStatusCode.OK);
		requests.Count(item => item.Target.EndsWith("/ServiceModel/AuthService.svc/Login"))
			.Should().Be(2);
		requests.Count(item => item.Target == "/data").Should().Be(2);
	}

	[Test]
	public async Task FirstCookieRequestForbidden_ShouldReturnResponseWithoutReplay()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			LoginResponse("session-one", "csrf-one"),
			new ScriptedResponse(StatusCode: 403));
		using CreatioClient client = new(server.BaseUri.ToString(), "user", "password") { SkipPing = true };

		using HttpResponseMessage response = await client.ExecuteGetRequestAsync(
			new Uri(server.BaseUri, "data").ToString());
		IReadOnlyList<CapturedRequest> requests = await capture;

		response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
		requests.Count(item => item.Target.EndsWith("/ServiceModel/AuthService.svc/Login"))
			.Should().Be(1);
		requests.Count(item => item.Target == "/data").Should().Be(1);
	}

	[Test]
	public async Task ExpiredNtlmSessionUnauthorized_ShouldReloginAndReplayRequest()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			LoginResponse("session-one", "csrf-one"),
			new ScriptedResponse(Body: "first"),
			new ScriptedResponse(StatusCode: 401),
			LoginResponse("session-two", "csrf-two"),
			new ScriptedResponse(Body: "recovered"));
		using CreatioClient client = new(server.BaseUri.ToString(), true,
			new NetworkCredential("windows-user", "windows-password")) { SkipPing = true };

		using (HttpResponseMessage first = await client.ExecuteGetRequestAsync(
			new Uri(server.BaseUri, "first").ToString())) { }
		using HttpResponseMessage recovered = await client.ExecuteGetRequestAsync(
			new Uri(server.BaseUri, "data").ToString());
		IReadOnlyList<CapturedRequest> requests = await capture;

		recovered.StatusCode.Should().Be(HttpStatusCode.OK);
		requests.Count(item => item.Target == "/Login/NuiLogin.aspx?ntlmlogin").Should().Be(2);
		requests.Count(item => item.Target == "/data").Should().Be(2);
	}

	[Test]
	public async Task ConcurrentExpiredSessionRecoveries_ShouldShareOneLogin()
	{
		Uri appUri = new("https://creatio.test/");
		CookieContainer cookies = new();
		cookies.Add(appUri, new Cookie(".ASPXAUTH", "session-one"));
		cookies.Add(appUri, new Cookie("BPMCSRF", "csrf-one"));
		CoordinatedLoginHandler inner = new(appUri, cookies);
		using CreatioAuthenticationHandler authentication = new(appUri, cookies, "user", "password", null,
			() => null, () => 0, () => true, true, inner);

		Task first = authentication.RecoverExpiredSessionAsync(0, CancellationToken.None);
		await inner.LoginStarted;
		Task second = authentication.RecoverExpiredSessionAsync(0, CancellationToken.None);
		inner.ReleaseLogin();
		await Task.WhenAll(first, second);

		inner.LoginCount.Should().Be(1);
	}

	[Test]
	public async Task ConcurrentExpiredOAuthRecoveries_ShouldShareOneTokenRefresh()
	{
		int refreshCount = 0;
		TaskCompletionSource<bool> refreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> releaseRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
		async Task Refresh(CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref refreshCount);
			refreshStarted.TrySetResult(true);
			await releaseRefresh.Task.WaitAsync(cancellationToken);
		}
		using CreatioAuthenticationHandler authentication = new(new Uri("https://creatio.test/"),
			new CookieContainer(), null, null, null, () => "token", () => null, () => true, true,
			new SequenceHandler(), Refresh);

		Task first = authentication.RecoverExpiredBearerTokenAsync(0, CancellationToken.None);
		await refreshStarted.Task;
		Task second = authentication.RecoverExpiredBearerTokenAsync(0, CancellationToken.None);
		releaseRefresh.TrySetResult(true);
		await Task.WhenAll(first, second);

		refreshCount.Should().Be(1);
	}

	[Test]
	public async Task ConcurrentSessionRefreshWithForbiddenResponse_ShouldRequestReplayWithoutSecondLogin()
	{
		Uri appUri = new("https://creatio.test/");
		CookieContainer cookies = new();
		cookies.Add(appUri, new Cookie(".ASPXAUTH", "session-one"));
		cookies.Add(appUri, new Cookie("BPMCSRF", "csrf-one"));
		ConcurrentSessionRefreshHandler inner = new(appUri, cookies);
		using CreatioAuthenticationHandler authentication = new(appUri, cookies, "user", "password", null,
			() => null, () => 0, () => true, true, inner);
		using HttpMessageInvoker invoker = new(authentication);

		Task<HttpResponseMessage> send = invoker.SendAsync(
			new HttpRequestMessage(HttpMethod.Get, new Uri(appUri, "data")), CancellationToken.None);
		await inner.RequestReceived;
		await authentication.RecoverExpiredSessionAsync(0, CancellationToken.None);
		inner.ReleaseResponse();

		CreatioSessionExpiredException exception = (await FluentActions.Awaiting(() => send)
			.Should().ThrowAsync<CreatioSessionExpiredException>()).Which;
		exception.AuthenticationGeneration.Should().Be(0);
		exception.Response.Dispose();
		inner.LoginCount.Should().Be(1);
	}

	[Test]
	public async Task ExpiredCookieSessionAfterRelogin_ShouldReturnSecondUnauthorizedResponse()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			LoginResponse("session-one", "csrf-one"),
			new ScriptedResponse(Body: "first"),
			new ScriptedResponse(StatusCode: 401),
			LoginResponse("session-two", "csrf-two"),
			new ScriptedResponse(StatusCode: 401));
		using CreatioClient client = new(server.BaseUri.ToString(), "user", "password") { SkipPing = true };

		using (HttpResponseMessage first = await client.ExecuteGetRequestAsync(
			new Uri(server.BaseUri, "first").ToString())) { }
		using HttpResponseMessage response = await client.ExecuteGetRequestAsync(
			new Uri(server.BaseUri, "data").ToString());
		IReadOnlyList<CapturedRequest> requests = await capture;

		response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
		requests.Count(item => item.Target.EndsWith("/ServiceModel/AuthService.svc/Login"))
			.Should().Be(2);
		requests.Count(item => item.Target == "/data").Should().Be(2);
	}

	[Test]
	public async Task RecoverExpiredSessionAfterAnotherLogin_ShouldNotAuthenticateAgain()
	{
		Uri appUri = new("https://creatio.test/");
		SequenceHandler inner = new(_ => Response(HttpStatusCode.OK, "ok"));
		using CreatioAuthenticationHandler authentication = new(appUri, new CookieContainer(), "user",
			"password", null, () => null, () => 0, () => true, true, inner);

		await authentication.RecoverExpiredSessionAsync(-1, CancellationToken.None);

		inner.Requests.Should().BeEmpty();
	}

	[Test]
	public async Task ExpiredCookieSessionRelativeRedirectWithoutRequestMessage_ShouldUseAppOrigin()
	{
		Uri appUri = new("https://creatio.test/");
		CookieContainer cookies = new();
		cookies.Add(appUri, new Cookie(".ASPXAUTH", "session-one"));
		cookies.Add(appUri, new Cookie("BPMCSRF", "csrf-one"));
		SequenceHandler inner = new(_ => new HttpResponseMessage(HttpStatusCode.Redirect) {
			Headers = { Location = new Uri("/Login/Login.html", UriKind.Relative) }
		});
		using CreatioAuthenticationHandler authentication = new(appUri, cookies, "user",
			"password", null, () => null, () => 0, () => true, true, inner);
		using HttpMessageInvoker invoker = new(authentication);

		Func<Task> act = async () => {
			using HttpResponseMessage response = await invoker.SendAsync(
				new HttpRequestMessage(HttpMethod.Get, new Uri(appUri, "data")), CancellationToken.None);
		};

		CreatioSessionExpiredException exception = (await act.Should()
			.ThrowAsync<CreatioSessionExpiredException>()).Which;
		exception.Response.Dispose();
	}

	[Test]
	public async Task SameHostHttpToHttpsRedirect_ShouldRemainTrusted()
	{
		Uri appUri = new("http://creatio.test/");
		SequenceHandler inner = new(
			request => {
				HttpResponseMessage response = Response(HttpStatusCode.Redirect, string.Empty);
				response.Headers.Location = new Uri("https://creatio.test/final");
				return response;
			},
			request => Response(HttpStatusCode.OK, "final"));
		using CreatioAuthenticationHandler authentication = new(appUri, new CookieContainer(), null, null,
			null, () => "token", () => null, () => true, true, inner);
		using HttpMessageInvoker invoker = new(authentication);

		using HttpResponseMessage response = await invoker.SendAsync(
			new HttpRequestMessage(HttpMethod.Get, appUri), CancellationToken.None);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		inner.Requests.Select(item => item.RequestUri!.Scheme).Should().ContainInOrder("http", "https");
	}

	[Test]
	public async Task SameHostHttpsAlternatePort_ShouldNotBeTrustedAsAnUpgrade()
	{
		Uri appUri = new("http://creatio.test/");
		SequenceHandler inner = new(request => Response(HttpStatusCode.OK, "unexpected"));
		using CreatioAuthenticationHandler authentication = new(appUri, new CookieContainer(), null, null,
			null, () => "token", () => null, () => true, true, inner);
		using HttpMessageInvoker invoker = new(authentication);

		Func<Task> act = async () => {
			using HttpResponseMessage response = await invoker.SendAsync(
				new HttpRequestMessage(HttpMethod.Get, "https://creatio.test:4443/data"), CancellationToken.None);
		};

		await act.Should().ThrowAsync<InvalidOperationException>();
		inner.Requests.Should().BeEmpty();
	}

	[Test]
	public async Task EmptyChunkedUploadResponse_ShouldAlwaysHaveReadableContent()
	{
		string path = Path.GetTempFileName();
		try {
			using CreatioClient client = new("https://example.invalid", "token");

			using HttpResponseMessage response = await client.UploadAlmFileByChunkAsync(
				"https://example.invalid/upload", path);

			response.StatusCode.Should().Be(HttpStatusCode.NoContent);
			(await response.Content.ReadAsStringAsync()).Should().BeEmpty();
		}
		finally {
			File.Delete(path);
		}
	}

	[TestCase(301)]
	[TestCase(302)]
	[TestCase(303)]
	public async Task Redirect_ShouldConvertMutableRequestToGet(int statusCode)
	{
		Uri appUri = new("https://creatio.test/");
		SequenceHandler inner = new(
			request => {
				HttpResponseMessage response = Response((HttpStatusCode)statusCode, string.Empty);
				response.Headers.Location = new Uri("/final", UriKind.Relative);
				return response;
			},
			request => Response(HttpStatusCode.OK, "final"));
		using CreatioAuthenticationHandler authentication = new(appUri, new CookieContainer(), null, null,
			null, () => "token", () => null, () => true, true, inner);
		using HttpMessageInvoker invoker = new(authentication);

		using HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, appUri) {
			Content = new StringContent("body")
		}, CancellationToken.None);

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		inner.Requests.Select(item => item.Method).Should().ContainInOrder(HttpMethod.Post, HttpMethod.Get);
	}

	[TestCase(307)]
	[TestCase(308)]
	public async Task RedirectThatRequiresReplayingContent_ShouldReturnRedirectResponse(int statusCode)
	{
		Uri appUri = new("https://creatio.test/");
		SequenceHandler inner = new(request => {
			HttpResponseMessage response = Response((HttpStatusCode)statusCode, string.Empty);
			response.Headers.Location = new Uri("/final", UriKind.Relative);
			return response;
		});
		using CreatioAuthenticationHandler authentication = new(appUri, new CookieContainer(), null, null,
			null, () => "token", () => null, () => true, true, inner);
		using HttpMessageInvoker invoker = new(authentication);

		using HttpResponseMessage response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, appUri) {
			Content = new StringContent("body")
		}, CancellationToken.None);

		((int)response.StatusCode).Should().Be(statusCode);
		inner.Requests.Should().ContainSingle();
	}

	[Test]
	public async Task TooManyRedirects_ShouldFailAfterFiftyResponses()
	{
		Uri appUri = new("https://creatio.test/");
		Func<HttpRequestMessage, HttpResponseMessage>[] redirects = Enumerable.Range(0, 50)
			.Select<int, Func<HttpRequestMessage, HttpResponseMessage>>(_ => request => {
				HttpResponseMessage response = Response(HttpStatusCode.Redirect, string.Empty);
				response.Headers.Location = new Uri("/again", UriKind.Relative);
				return response;
			})
			.ToArray();
		SequenceHandler inner = new(redirects);
		using CreatioAuthenticationHandler authentication = new(appUri, new CookieContainer(), null, null,
			null, () => "token", () => null, () => true, true, inner);
		using HttpMessageInvoker invoker = new(authentication);

		Func<Task> act = async () => {
			using HttpResponseMessage response = await invoker.SendAsync(
				new HttpRequestMessage(HttpMethod.Get, appUri), CancellationToken.None);
		};

		await act.Should().ThrowAsync<HttpRequestException>();
		inner.Requests.Should().HaveCount(50);
	}

	[Test]
	public async Task DownloadFileByGetAsync_ShouldApplyTimeoutToTheResponseBody()
	{
		string path = Path.GetTempFileName();
		try {
			await using ScriptedLoopbackHttpServer server = new();
			Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
				new ScriptedResponse(Body: "late-body", BodyDelay: TimeSpan.FromMilliseconds(400)));
			using CreatioClient client = new(server.BaseUri.ToString(), "token");

			Func<Task> act = async () => {
				using HttpResponseMessage response = await client.DownloadFileByGetAsync(
					server.BaseUri.ToString(), path, requestTimeout: 50);
			};

			await act.Should().ThrowAsync<OperationCanceledException>();
			await capture;
		}
		finally {
			File.Delete(path);
		}
	}

	[Test]
	public async Task ExecutePostRequestAsync_ShouldRecreateBodyForRetry()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			new ScriptedResponse(CloseWithoutResponse: true, KeepListeningAfterClose: true),
			new ScriptedResponse(Body: "ok"));
		using CreatioClient client = new(server.BaseUri.ToString(), "token");

		using HttpResponseMessage response = await client.ExecutePostRequestAsync(server.BaseUri.ToString(),
			"{\"value\":42}", maxAttempts: 2, delaySec: 0);
		IReadOnlyList<CapturedRequest> requests = await capture;

		requests.Should().HaveCount(2);
		requests.Select(request => request.Body).Should().OnlyContain(body =>
			System.Text.Encoding.UTF8.GetString(body) == "{\"value\":42}");
	}

	[Test]
	public async Task EnsureAuthenticatedForRequestAsync_ShouldAcceptPreexistingSessionCookie()
	{
		Uri appUri = new("https://creatio.test/");
		CookieContainer cookies = new();
		cookies.Add(appUri, new Cookie(".ASPXAUTH", "session-token"));
		SequenceHandler inner = new();
		using CreatioAuthenticationHandler authentication = new(appUri, cookies, "user", "password", null,
			() => null, () => null, () => true, true, inner);

		await authentication.EnsureAuthenticatedForRequestAsync(CancellationToken.None);

		inner.Requests.Should().BeEmpty();
	}

	private static HttpResponseMessage Response(HttpStatusCode statusCode, string body) => new(statusCode) {
		Content = new StringContent(body)
	};

	private sealed class SequenceHandler : HttpMessageHandler
	{
		private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

		public SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
		{
			_responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
		}

		public List<HttpRequestMessage> Requests { get; } = new();

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			Requests.Add(request);
			return Task.FromResult(_responses.Dequeue()(request));
		}
	}

	private sealed class DelayedIgnoringCancellationHandler : HttpMessageHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			await Task.Delay(100);
			return Response(HttpStatusCode.OK, "late");
		}
	}

	private sealed class ConcurrentSessionRefreshHandler : HttpMessageHandler
	{
		private readonly Uri _appUri;
		private readonly CookieContainer _cookies;
		private readonly TaskCompletionSource<bool> _requestReceived =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource<bool> _release =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public ConcurrentSessionRefreshHandler(Uri appUri, CookieContainer cookies)
		{
			_appUri = appUri;
			_cookies = cookies;
		}

		public Task RequestReceived => _requestReceived.Task;

		public int LoginCount { get; private set; }

		public void ReleaseResponse() => _release.TrySetResult(true);

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			if (request.RequestUri.AbsolutePath.EndsWith("/ServiceModel/AuthService.svc/Login",
				StringComparison.Ordinal)) {
				LoginCount++;
				_cookies.Add(_appUri, new Cookie(".ASPXAUTH", "session-two"));
				_cookies.Add(_appUri, new Cookie("BPMCSRF", "csrf-two"));
				return Response(HttpStatusCode.OK, "{\"Code\":0}");
			}
			_requestReceived.TrySetResult(true);
			await _release.Task.WaitAsync(cancellationToken);
			return Response(HttpStatusCode.Forbidden, "forbidden");
		}
	}

	private sealed class CoordinatedLoginHandler : HttpMessageHandler
	{
		private readonly Uri _appUri;
		private readonly CookieContainer _cookies;
		private readonly TaskCompletionSource<bool> _loginStarted =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource<bool> _releaseLogin =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public CoordinatedLoginHandler(Uri appUri, CookieContainer cookies)
		{
			_appUri = appUri;
			_cookies = cookies;
		}

		public Task LoginStarted => _loginStarted.Task;

		public int LoginCount { get; private set; }

		public void ReleaseLogin() => _releaseLogin.TrySetResult(true);

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			LoginCount++;
			_loginStarted.TrySetResult(true);
			await _releaseLogin.Task.WaitAsync(cancellationToken);
			_cookies.Add(_appUri, new Cookie(".ASPXAUTH", "session-two"));
			_cookies.Add(_appUri, new Cookie("BPMCSRF", "csrf-two"));
			return Response(HttpStatusCode.OK, "{\"Code\":0}");
		}
	}

	private sealed class ThrowingContent : HttpContent
	{
		public bool IsDisposed { get; private set; }

		protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) =>
			Task.FromException(new IOException("simulated body failure"));

		protected override bool TryComputeLength(out long length)
		{
			length = 0;
			return false;
		}

		protected override void Dispose(bool disposing)
		{
			IsDisposed = true;
			base.Dispose(disposing);
		}
	}

	private static ScriptedResponse LoginResponse(string sessionToken = "session-token",
		string csrfToken = "csrf-token") => new(
		Body: "{\"Code\":0}",
		Headers: new Dictionary<string, string[]> {
			["Set-Cookie"] = new[] {
				$".ASPXAUTH={sessionToken}; Path=/",
				$"BPMCSRF={csrfToken}; Path=/"
			}
		});
}
