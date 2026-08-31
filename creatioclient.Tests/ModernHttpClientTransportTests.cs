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

	[Test]
	public async Task LoginAsync_ShouldApplyTimeoutWhileReadingResponseBody()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			LoginResponse() with { BodyDelay = TimeSpan.FromMilliseconds(400) });
		using CreatioClient client = new(server.BaseUri.ToString(), "user", "password");

		Func<Task> act = async () => {
			using HttpResponseMessage response = await client.LoginAsync(requestTimeout: 50);
		};

		await act.Should().ThrowAsync<OperationCanceledException>();
		await capture;
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
