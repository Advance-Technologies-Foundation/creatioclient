using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Creatio.Client.Tests;

[TestFixture]
public class CreatioClientLoginTests
{
	[TestCase(-120)]
	[TestCase(0)]
	[TestCase(330)]
	[Description("Verifies that the additive password constructor preserves a caller-selected time zone offset")]
	public void Constructor_ShouldSetExplicitOffset_WhenTimeZoneOffsetIsProvided(int explicitOffset)
	{
		// Arrange
		CreatioClient client = new("https://example.invalid", "user", "password", explicitOffset);

		// Act
		int? actualOffset = client.TimeZoneOffset;

		// Assert
		actualOffset.Should().Be(explicitOffset,
			because: "the constructor is a convenience for the same explicit property contract");
	}

	[TestCase(false)]
	[TestCase(true)]
	[Description("Verifies that both password login overloads default to the browser-equivalent current local time zone offset")]
	public async Task Login_ShouldSendCurrentBrowserOffset_WhenTimeZoneOffsetIsNotSet(bool useTimeoutOverload)
	{
		// Arrange
		await using LoginLoopbackHttpServer server = new();
		Task<RecordedRequest> capturedRequest = server.ReceiveLoginAndRespondAsync();
		CreatioClient client = new(server.BaseUri.ToString(), "user", "password");
		int offsetBeforeLogin = GetBrowserTimeZoneOffset();

		// Act
		if (useTimeoutOverload) {
			client.Login(10_000);
		} else {
			client.Login();
		}
		int offsetAfterLogin = GetBrowserTimeZoneOffset();
		RecordedRequest request = await capturedRequest;
		JObject payload = JObject.Parse(request.Body);

		// Assert
		new[] { offsetBeforeLogin, offsetAfterLogin }.Should().Contain((int)payload["TimeZoneOffset"]!,
			because: "the default must match JavaScript Date.getTimezoneOffset() at login time, including DST");
		((string)payload["UserName"]!).Should().Be("user",
			because: "adding the offset must preserve the configured username");
		((string)payload["UserPassword"]!).Should().Be("password",
			because: "adding the offset must preserve the configured password");
	}

	[TestCase(-120)]
	[TestCase(0)]
	[TestCase(330)]
	[Description("Verifies that caller-selected time zone offsets are sent without applying the default")]
	public async Task Login_ShouldPreserveExplicitOffset_WhenTimeZoneOffsetIsSet(int explicitOffset)
	{
		// Arrange
		await using LoginLoopbackHttpServer server = new();
		Task<RecordedRequest> capturedRequest = server.ReceiveLoginAndRespondAsync();
		CreatioClient client = new(server.BaseUri.ToString(), "user", "password") {
			TimeZoneOffset = explicitOffset
		};

		// Act
		client.Login();
		RecordedRequest request = await capturedRequest;
		JObject payload = JObject.Parse(request.Body);

		// Assert
		((int)payload["TimeZoneOffset"]!).Should().Be(explicitOffset,
			because: "an explicit offset, including zero, must remain under caller control");
	}

	[TestCase(false)]
	[TestCase(true)]
	[Description("Verifies that both password login overloads use the same time zone-aware payload")]
	public async Task Login_ShouldSendExplicitOffset_WhenEitherOverloadIsUsed(bool useTimeoutOverload)
	{
		// Arrange
		const int explicitOffset = -345;
		await using LoginLoopbackHttpServer server = new();
		Task<RecordedRequest> capturedRequest = server.ReceiveLoginAndRespondAsync();
		CreatioClient client = new(server.BaseUri.ToString(), "user", "password") {
			TimeZoneOffset = explicitOffset
		};

		// Act
		if (useTimeoutOverload) {
			client.Login(10_000);
		} else {
			client.Login();
		}
		RecordedRequest request = await capturedRequest;
		JObject payload = JObject.Parse(request.Body);

		// Assert
		((int)payload["TimeZoneOffset"]!).Should().Be(explicitOffset,
			because: "both public password login overloads must send the same caller-selected offset");
	}

	[Test]
	[NonParallelizable]
	[Description("Verifies that negative offsets remain valid JSON under cultures with a non-ASCII negative sign")]
	public async Task Login_ShouldSerializeOffsetInvariantly_WhenCurrentCultureUsesNonAsciiNegativeSign()
	{
		// Arrange
		CultureInfo originalCulture = CultureInfo.CurrentCulture;
		CultureInfo hostileCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
		hostileCulture.NumberFormat.NegativeSign = "\u2212";
		await using LoginLoopbackHttpServer server = new();
		Task<RecordedRequest> capturedRequest = server.ReceiveLoginAndRespondAsync();
		CreatioClient client = new(server.BaseUri.ToString(), "user", "password") {
			TimeZoneOffset = -120
		};

		try {
			// Act
			CultureInfo.CurrentCulture = hostileCulture;
			client.Login();
			RecordedRequest request = await capturedRequest;
			JObject payload = JObject.Parse(request.Body);

			// Assert
			((int)payload["TimeZoneOffset"]!).Should().Be(-120,
				because: "JSON numeric syntax requires an ASCII minus sign regardless of the process culture");
		} finally {
			CultureInfo.CurrentCulture = originalCulture;
		}
	}

	private static int GetBrowserTimeZoneOffset() => -(int)DateTimeOffset.Now.Offset.TotalMinutes;

	private sealed record RecordedRequest(string Method, IReadOnlyDictionary<string, string> Headers, string Body);

	private sealed class LoginLoopbackHttpServer : IAsyncDisposable
	{
		private readonly TcpListener _listener;

		public LoginLoopbackHttpServer()
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

		public async Task<RecordedRequest> ReceiveLoginAndRespondAsync()
		{
			using TcpClient client = await _listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
			await using NetworkStream stream = client.GetStream();
			RecordedRequest request = await ReadRequestAsync(stream);
			await WriteLoginResponseAsync(stream);
			return request;
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

			int contentLength = headers.TryGetValue("Content-Length", out string length)
				? int.Parse(length, CultureInfo.InvariantCulture)
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

			return new RecordedRequest(requestLine[0], headers, Encoding.UTF8.GetString(bodyBytes));
		}

		private static bool EndsWithHeaderTerminator(IReadOnlyList<byte> bytes) =>
			bytes.Count >= 4
			&& bytes[^4] == '\r'
			&& bytes[^3] == '\n'
			&& bytes[^2] == '\r'
			&& bytes[^1] == '\n';

		private static async Task WriteLoginResponseAsync(NetworkStream stream)
		{
			byte[] bodyBytes = Encoding.UTF8.GetBytes("{\"Code\":0}");
			StringBuilder response = new();
			response.Append("HTTP/1.1 200 OK\r\n");
			response.Append("Content-Type: application/json\r\n");
			response.Append("Set-Cookie: .ASPXAUTH=session-token; Path=/\r\n");
			response.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
			response.Append("Connection: close\r\n\r\n");
			await stream.WriteAsync(Encoding.ASCII.GetBytes(response.ToString()));
			await stream.WriteAsync(bodyBytes);
			await stream.FlushAsync();
		}
	}
}
