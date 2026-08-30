using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Creatio.Client.Tests;

internal sealed record ScriptedResponse(
	int StatusCode = 200,
	string Body = "",
	IReadOnlyDictionary<string, string[]> Headers = null,
	TimeSpan? Delay = null,
	TimeSpan? BodyDelay = null,
	bool CloseWithoutResponse = false,
	bool KeepListeningAfterClose = false,
	string ContentType = "application/json",
	byte[] BodyBytes = null,
	int? DeclaredContentLength = null);

internal sealed record CapturedRequest(
	string Method,
	string Target,
	IReadOnlyDictionary<string, string> Headers,
	byte[] Body);

internal sealed class ScriptedLoopbackHttpServer : IAsyncDisposable
{
	private readonly TcpListener _listener;

	public ScriptedLoopbackHttpServer()
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

	public bool HasPendingConnection => _listener.Pending();

	public async Task<IReadOnlyList<CapturedRequest>> CaptureAsync(params ScriptedResponse[] responses)
	{
		List<CapturedRequest> requests = new();
		foreach (ScriptedResponse response in responses) {
			using TcpClient client = await _listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
			await using NetworkStream stream = client.GetStream();
			requests.Add(await ReadRequestAsync(stream));
			if (response.Delay.HasValue) {
				await Task.Delay(response.Delay.Value);
			}
			if (!response.CloseWithoutResponse) {
				try {
					await WriteResponseAsync(stream, response);
				}
				catch (IOException)
				{
					// A timeout characterization may close the client before the scripted server responds.
				}
			} else if (!response.KeepListeningAfterClose) {
				_listener.Stop();
			}
		}
		return requests;
	}

	public ValueTask DisposeAsync()
	{
		_listener.Stop();
		return ValueTask.CompletedTask;
	}

	private static async Task<CapturedRequest> ReadRequestAsync(NetworkStream stream)
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
			await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"));
			await stream.FlushAsync();
		}

		int contentLength = headers.TryGetValue("Content-Length", out string length)
			? int.Parse(length, CultureInfo.InvariantCulture)
			: 0;
		byte[] body = new byte[contentLength];
		int offset = 0;
		while (offset < body.Length) {
			int read = await stream.ReadAsync(body.AsMemory(offset));
			if (read == 0) {
				throw new EndOfStreamException("Connection closed before the HTTP body was complete.");
			}
			offset += read;
		}

		return new CapturedRequest(requestLine[0], requestLine[1], headers, body);
	}

	private static bool EndsWithHeaderTerminator(IReadOnlyList<byte> bytes) =>
		bytes.Count >= 4
		&& bytes[^4] == '\r'
		&& bytes[^3] == '\n'
		&& bytes[^2] == '\r'
		&& bytes[^1] == '\n';

	private static async Task WriteResponseAsync(NetworkStream stream, ScriptedResponse response)
	{
		byte[] body = response.BodyBytes ?? Encoding.UTF8.GetBytes(response.Body);
		StringBuilder text = new();
		text.Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ')
			.Append(GetReasonPhrase(response.StatusCode)).Append("\r\n");
		text.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
		if (response.Headers != null) {
			foreach ((string name, string[] values) in response.Headers) {
				foreach (string value in values) {
					text.Append(name).Append(": ").Append(value).Append("\r\n");
				}
			}
		}
		text.Append("Content-Length: ").Append(response.DeclaredContentLength ?? body.Length).Append("\r\n");
		text.Append("Connection: close\r\n\r\n");
		await stream.WriteAsync(Encoding.ASCII.GetBytes(text.ToString()));
		await stream.FlushAsync();
		if (response.BodyDelay.HasValue) {
			await Task.Delay(response.BodyDelay.Value);
		}
		await stream.WriteAsync(body);
		await stream.FlushAsync();
	}

	private static string GetReasonPhrase(int statusCode) => statusCode switch {
		200 => "OK",
		201 => "Created",
		204 => "No Content",
		400 => "Bad Request",
		401 => "Unauthorized",
		404 => "Not Found",
		500 => "Internal Server Error",
		_ => "Response"
	};
}
