using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace Creatio.Client.Tests;

[TestFixture]
public class CreatioClientMutableRequestTests
{
	[Test]
	[Description("Verifies that PUT and PATCH expose matching timeout defaults on both public call surfaces")]
	public void ExecutePutRequest_ShouldMatchPatchTimeoutDefaults_WhenTimeoutIsOmitted()
	{
		// Arrange
		ParameterInfo concretePatchTimeout = GetTimeoutParameter(typeof(CreatioClient), nameof(CreatioClient.ExecutePatchRequest));
		ParameterInfo concretePutTimeout = GetTimeoutParameter(typeof(CreatioClient), nameof(CreatioClient.ExecutePutRequest));
		ParameterInfo interfacePatchTimeout = GetTimeoutParameter(typeof(ICreatioClient), nameof(ICreatioClient.ExecutePatchRequest));
		ParameterInfo interfacePutTimeout = GetTimeoutParameter(typeof(ICreatioClient), nameof(ICreatioClient.ExecutePutRequest));

		// Act
		object concretePatchDefault = concretePatchTimeout.DefaultValue!;
		object concretePutDefault = concretePutTimeout.DefaultValue!;
		object interfacePatchDefault = interfacePatchTimeout.DefaultValue!;
		object interfacePutDefault = interfacePutTimeout.DefaultValue!;

		// Assert
		concretePutDefault.Should().Be(concretePatchDefault,
			because: "PUT must preserve the existing concrete PATCH timeout behavior");
		interfacePutDefault.Should().Be(interfacePatchDefault,
			because: "PUT must preserve the existing interface PATCH timeout behavior");
	}

	[TestCase("PATCH")]
	[TestCase("PUT")]
	[Description("Verifies that mutable requests preserve their verb, JSON body, and OAuth bearer authentication")]
	public async Task ExecuteMutableRequest_ShouldSendJsonAndBearerToken_WhenOAuthTokenIsConfigured(string method)
	{
		// Arrange
		const string requestBody = "{\"Name\":\"Updated\"}";
		await using LoopbackHttpServer server = new();
		Task<RecordedRequest> capturedRequest = server.ReceiveAndRespondAsync("{\"ok\":true}");
		CreatioClient client = new(server.BaseUri.ToString(), "test-token");

		// Act
		string result = method == "PUT"
			? client.ExecutePutRequest(server.BaseUri.ToString(), requestBody)
			: client.ExecutePatchRequest(server.BaseUri.ToString(), requestBody);
		RecordedRequest request = await capturedRequest;

		// Assert
		result.Should().Be("{\"ok\":true}", because: "the mutable request should return the response body");
		request.Method.Should().Be(method, because: "the requested HTTP verb must reach Creatio unchanged");
		request.Body.Should().Be(requestBody, because: "the JSON payload must reach Creatio unchanged");
		request.Headers["Content-Type"].Should().StartWith("application/json",
			because: "mutable request bodies are JSON");
		request.Headers["Authorization"].Should().Be("Bearer test-token",
			because: "OAuth clients authenticate with their bearer token");
		request.Headers.Should().NotContainKey("BPMCSRF",
			because: "OAuth requests must not use cookie-session CSRF authentication");
	}

	[Test]
	[Description("Verifies that PUT sends the login cookies and BPMCSRF header for cookie authentication")]
	public async Task ExecutePutRequest_ShouldSendCookiesAndCsrfHeader_WhenCookieAuthenticationIsConfigured()
	{
		// Arrange
		const string requestBody = "{\"Name\":\"Updated\"}";
		await using LoopbackHttpServer server = new();
		Task<RecordedRequest> putRequest = server.ReceiveAndRespondAsync(string.Empty);
		CreatioClient client = new(server.BaseUri.ToString(), "user", "password");
		SetAuthenticationCookies(client, server.BaseUri);

		// Act
		string result = client.ExecutePutRequest(server.BaseUri.ToString(), requestBody);
		RecordedRequest put = await putRequest;

		// Assert
		result.Should().BeEmpty(because: "Creatio commonly returns an empty body after a successful update");
		put.Method.Should().Be("PUT", because: "PUT must not be aliased to PATCH");
		put.Body.Should().Be(requestBody, because: "the JSON payload must reach Creatio unchanged");
		put.Headers["Cookie"].Should().Contain(".ASPXAUTH=session-token",
			because: "the authenticated session cookie must accompany the update");
		put.Headers["Cookie"].Should().Contain("BPMCSRF=csrf-token",
			because: "the CSRF cookie belongs to the authenticated session");
		put.Headers["BPMCSRF"].Should().Be("csrf-token",
			because: "cookie-authenticated mutable requests require the matching CSRF header");
		put.Headers.Should().NotContainKey("Authorization",
			because: "cookie authentication must not fabricate an OAuth bearer token");
	}

	[Test]
	[Description("Verifies that PUT retries after a transport failure and recreates the request body")]
	public async Task ExecutePutRequest_ShouldRetryWithBody_WhenFirstTransportAttemptFails()
	{
		// Arrange
		const string requestBody = "{\"Name\":\"Retried\"}";
		await using LoopbackHttpServer server = new();
		Task<(RecordedRequest TimedOutAttempt, RecordedRequest SuccessfulAttempt)> attempts =
			server.TimeoutThenRespondAsync("retry-ok");
		CreatioClient client = new(server.BaseUri.ToString(), "test-token");

		// Act
		string result = client.ExecutePutRequest(
			server.BaseUri.ToString(), requestBody, requestTimeout: 2000, maxAttempts: 2, delaySec: 0);
		(RecordedRequest timedOutAttempt, RecordedRequest successfulAttempt) = await attempts;

		// Assert
		result.Should().Be("retry-ok", because: "the second transport attempt should succeed");
		timedOutAttempt.Method.Should().Be("PUT", because: "the timed-out attempt must use the requested HTTP verb");
		timedOutAttempt.Body.Should().Be(requestBody, because: "the first attempt must send the JSON content");
		successfulAttempt.Method.Should().Be("PUT", because: "the retried request must preserve the HTTP verb");
		successfulAttempt.Body.Should().Be(requestBody, because: "the retried request must recreate the JSON content");
	}

	private static ParameterInfo GetTimeoutParameter(Type owner, string methodName) =>
		owner.GetMethod(methodName)!.GetParameters().Single(parameter => parameter.Name == "requestTimeout");

	private static void SetAuthenticationCookies(CreatioClient client, Uri baseUri)
	{
		CookieContainer cookies = new();
		cookies.Add(baseUri, new Cookie(".ASPXAUTH", "session-token"));
		cookies.Add(baseUri, new Cookie("BPMCSRF", "csrf-token"));
		typeof(CreatioClient)
			.GetField("_authCookie", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(client, cookies);
	}

	private sealed record RecordedRequest(
		string Method,
		IReadOnlyDictionary<string, string> Headers,
		string Body);

	private sealed class LoopbackHttpServer : IAsyncDisposable
	{
		private readonly TcpListener _listener;

		public LoopbackHttpServer()
		{
			_listener = new TcpListener(IPAddress.Loopback, 0);
			_listener.Start();
		}

		public Uri BaseUri {
			get {
				IPEndPoint endpoint = (IPEndPoint)_listener.LocalEndpoint;
				return new Uri($"http://127.0.0.1:{endpoint.Port}/");
			}
		}

		public async Task<RecordedRequest> ReceiveAndRespondAsync(string responseBody)
		{
			using TcpClient client = await _listener.AcceptTcpClientAsync()
				.WaitAsync(TimeSpan.FromSeconds(10));
			await using NetworkStream stream = client.GetStream();
			RecordedRequest request = await ReadRequestAsync(stream);
			await WriteResponseAsync(stream, responseBody);
			return request;
		}

		public async Task<(RecordedRequest TimedOutAttempt, RecordedRequest SuccessfulAttempt)> TimeoutThenRespondAsync(
			string responseBody)
		{
			using (TcpClient client = await _listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10))) {
				await using NetworkStream stream = client.GetStream();
				RecordedRequest timedOutAttempt = await ReadRequestAsync(stream);
				Task<RecordedRequest> successfulAttempt = ReceiveAndRespondAsync(responseBody);
				return (timedOutAttempt, await successfulAttempt);
			}
		}

		public ValueTask DisposeAsync()
		{
			_listener.Stop();
			return ValueTask.CompletedTask;
		}

		private static async Task<RecordedRequest> ReadRequestAsync(NetworkStream stream)
		{
			List<byte> headerBytes = new();
			byte[] singleByte = new byte[1];
			while (!EndsWithHeaderTerminator(headerBytes)) {
				int read = await stream.ReadAsync(singleByte);
				if (read == 0) {
					throw new EndOfStreamException("Connection closed before the HTTP headers were complete.");
				}
				headerBytes.Add(singleByte[0]);
				if (headerBytes.Count > 64 * 1024) {
					throw new InvalidDataException("HTTP headers exceeded the test server limit.");
				}
			}

			string headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
			string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
			string[] requestLine = lines[0].Split(' ');
			Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
			foreach (string line in lines.Skip(1)) {
				int separator = line.IndexOf(':');
				if (separator > 0) {
					headers[line.Substring(0, separator)] = line.Substring(separator + 1).Trim();
				}
			}

			if (headers.TryGetValue("Expect", out string expectation)
					&& expectation.Equals("100-continue", StringComparison.OrdinalIgnoreCase)) {
				byte[] continueResponse = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
				await stream.WriteAsync(continueResponse);
				await stream.FlushAsync();
			}

			int contentLength = headers.TryGetValue("Content-Length", out string length)
				? int.Parse(length)
				: 0;
			byte[] bodyBytes = new byte[contentLength];
			int offset = 0;
			while (offset < bodyBytes.Length) {
				int read = await stream.ReadAsync(bodyBytes.AsMemory(offset));
				if (read == 0) {
					throw new EndOfStreamException("Connection closed before the HTTP body was complete.");
				}
				offset += read;
			}

			return new RecordedRequest(
				requestLine[0],
				headers,
				Encoding.UTF8.GetString(bodyBytes));
		}

		private static bool EndsWithHeaderTerminator(IReadOnlyList<byte> bytes) =>
			bytes.Count >= 4
			&& bytes[^4] == '\r'
			&& bytes[^3] == '\n'
			&& bytes[^2] == '\r'
			&& bytes[^1] == '\n';

		private static async Task WriteResponseAsync(NetworkStream stream, string responseBody)
		{
			byte[] bodyBytes = Encoding.UTF8.GetBytes(responseBody);
			StringBuilder response = new();
			response.Append("HTTP/1.1 200 OK\r\n");
			response.Append("Content-Type: application/json\r\n");
			response.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
			response.Append("Connection: close\r\n\r\n");
			byte[] headerBytes = Encoding.ASCII.GetBytes(response.ToString());
			await stream.WriteAsync(headerBytes);
			await stream.WriteAsync(bodyBytes);
			await stream.FlushAsync();
		}
	}
}
