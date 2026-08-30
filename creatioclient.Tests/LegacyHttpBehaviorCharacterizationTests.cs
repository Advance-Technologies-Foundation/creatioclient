using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace Creatio.Client.Tests;

[TestFixture]
[Category("CompatibilityBaseline")]
public class LegacyHttpBehaviorCharacterizationTests
{
	[Test]
	public async Task ExecuteGetRequest_ShouldLazyLoginAndSendSessionCookiesAndCsrfHeader()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			LoginResponse(),
			new ScriptedResponse(Body: "get-ok"));
		CreatioClient client = new(server.BaseUri.ToString(), "user", "password") { SkipPing = true };

		string result = client.ExecuteGetRequest(new Uri(server.BaseUri, "data").ToString());
		IReadOnlyList<CapturedRequest> requests = await capture;

		result.Should().Be("get-ok");
		requests.Should().HaveCount(2);
		requests[0].Method.Should().Be("POST");
		requests[0].Target.Should().Be("/ServiceModel/AuthService.svc/Login");
		requests[1].Method.Should().Be("GET");
		requests[1].Headers["Cookie"].Should().Contain(".ASPXAUTH=session-token").And.Contain("BPMCSRF=csrf-token");
		requests[1].Headers["BPMCSRF"].Should().Be("csrf-token",
			because: "the 1.0.40 implementation sends BPMCSRF even on cookie-authenticated GET requests");
	}

	[Test]
	public async Task ExecuteGetRequest_ShouldReturnErrorBodyInsteadOfThrowing_WhenServerReturns500()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			new ScriptedResponse(StatusCode: 500, Body: "legacy-error"));
		CreatioClient client = new(server.BaseUri.ToString(), "token");

		string result = client.ExecuteGetRequest(server.BaseUri.ToString());
		await capture;

		result.Should().Be("legacy-error",
			because: "ATFWebRequestExtension converts WebException responses into their body strings");
	}

	[TestCase("POST")]
	[TestCase("PATCH")]
	[TestCase("PUT")]
	[TestCase("DELETE")]
	public async Task MutableRequests_ShouldReturnErrorBodyWithoutEnsuringSuccess(string method)
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			new ScriptedResponse(StatusCode: 500, Body: "mutable-error"));
		CreatioClient client = new(server.BaseUri.ToString(), "token");

		string result = method switch {
			"POST" => client.ExecutePostRequest(server.BaseUri.ToString(), "{}"),
			"PATCH" => client.ExecutePatchRequest(server.BaseUri.ToString(), "{}"),
			"PUT" => client.ExecutePutRequest(server.BaseUri.ToString(), "{}"),
			"DELETE" => client.ExecuteDeleteRequest(server.BaseUri.ToString(), "{}"),
			_ => throw new AssertionException($"Unsupported method {method}")
		};
		IReadOnlyList<CapturedRequest> requests = await capture;

		result.Should().Be("mutable-error");
		requests.Single().Method.Should().Be(method);
	}

	[Test]
	public async Task ExecuteGetRequest_ShouldReturnEmptyStringAndNotRetry_WhenTransportFailsWithWebException()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			new ScriptedResponse(CloseWithoutResponse: true));
		CreatioClient client = new(server.BaseUri.ToString(), "token");

		string result = client.ExecuteGetRequest(server.BaseUri.ToString(), maxAttempts: 2, delaySec: 0);
		IReadOnlyList<CapturedRequest> requests = await capture;

		result.Should().BeEmpty();
		requests.Should().ContainSingle(
			because: "GetServiceResponse swallows WebException before the outer retry loop can observe it");
	}

	[Test]
	public async Task ExecutePostRequest_ShouldExposeAggregateException_WhenRequestTimesOut()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			new ScriptedResponse(Body: "too-late", Delay: TimeSpan.FromMilliseconds(400)));
		CreatioClient client = new(server.BaseUri.ToString(), "token");

		Action act = () => client.ExecutePostRequest(server.BaseUri.ToString(), "{}", requestTimeout: 50);

		act.Should().Throw<AggregateException>(
			because: "the 1.0.40 synchronous HttpClient path blocks through Task.Result");
		await capture;
	}

	[Test]
	public async Task DownloadFileByGet_ShouldPreserveBinaryResponseBytes()
	{
		byte[] expected = { 0, 1, 2, 127, 128, 254, 255 };
		string path = Path.GetTempFileName();
		try
		{
			await using ScriptedLoopbackHttpServer server = new();
			Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
				new ScriptedResponse(ContentType: "application/octet-stream", BodyBytes: expected));
			CreatioClient client = new(server.BaseUri.ToString(), "token");

			client.DownloadFileByGet(server.BaseUri.ToString(), path);
			await capture;

			File.ReadAllBytes(path).Should().Equal(expected);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Test]
	public async Task DownloadFileByGet_ShouldThrowAndPreserveExistingFile_WhenServerReturns500()
	{
		string path = Path.GetTempFileName();
		byte[] original = { 1, 2, 3, 4 };
		await File.WriteAllBytesAsync(path, original);
		try {
			await using ScriptedLoopbackHttpServer server = new();
			Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
				new ScriptedResponse(StatusCode: 500, Body: "error-body"));
			CreatioClient client = new(server.BaseUri.ToString(), "token");

			Action act = () => client.DownloadFileByGet(server.BaseUri.ToString(), path);

			WebException exception = act.Should().Throw<WebException>().Which;
			exception.Status.Should().Be(WebExceptionStatus.ProtocolError);
			HttpWebResponse response = exception.Response.Should().BeAssignableTo<HttpWebResponse>().Subject;
			response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
			using (StreamReader reader = new(exception.Response.GetResponseStream())) {
				reader.ReadToEnd().Should().Be("error-body");
			}
			await capture;
			File.ReadAllBytes(path).Should().Equal(original);
		}
		finally {
			File.Delete(path);
		}
	}

	[Test]
	public async Task DownloadFileByGet_ShouldRetryErrorResponseAndCompleteCopy()
	{
		string path = Path.GetTempFileName();
		byte[] expected = { 9, 8, 7 };
		try {
			await using ScriptedLoopbackHttpServer server = new();
			Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
				new ScriptedResponse(StatusCode: 500, Body: "retry"),
				new ScriptedResponse(ContentType: "application/octet-stream", BodyBytes: expected));
			CreatioClient client = new(server.BaseUri.ToString(), "token");
			client.SetRetryPolicy(2, 0, RetryPolicy.Simple);

			client.DownloadFileByGet(server.BaseUri.ToString(), path);

			(await capture).Should().HaveCount(2);
			File.ReadAllBytes(path).Should().Equal(expected);
		}
		finally {
			File.Delete(path);
		}
	}

	[Test]
	public async Task DownloadFileByGet_ShouldApplyTimeoutWhileReadingErrorBody()
	{
		string path = Path.GetTempFileName();
		try {
			await using ScriptedLoopbackHttpServer server = new();
			Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
				new ScriptedResponse(StatusCode: 500, Body: "late-error",
					BodyDelay: TimeSpan.FromMilliseconds(400)));
			CreatioClient client = new(server.BaseUri.ToString(), "token");

			Action act = () => client.DownloadFileByGet(server.BaseUri.ToString(), path, requestTimeout: 50);

			act.Should().Throw<WebException>()
				.Which.Status.Should().Be(WebExceptionStatus.Timeout);
			await capture;
		}
		finally {
			File.Delete(path);
		}
	}

	[Test]
	public async Task DownloadFileByGet_ShouldRetryInterruptedBodyAndCompleteCopy()
	{
		string path = Path.GetTempFileName();
		byte[] expected = { 6, 5, 4, 3 };
		try {
			await using ScriptedLoopbackHttpServer server = new();
			Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
				new ScriptedResponse(Body: "partial", DeclaredContentLength: 100),
				new ScriptedResponse(ContentType: "application/octet-stream", BodyBytes: expected));
			CreatioClient client = new(server.BaseUri.ToString(), "token");
			client.SetRetryPolicy(2, 0, RetryPolicy.Simple);

			client.DownloadFileByGet(server.BaseUri.ToString(), path);

			(await capture).Should().HaveCount(2);
			File.ReadAllBytes(path).Should().Equal(expected);
		}
		finally {
			File.Delete(path);
		}
	}

	[Test]
	public async Task DownloadTimeout_ShouldStartAfterLegacyLazyAuthentication()
	{
		string path = Path.GetTempFileName();
		try {
			await using ScriptedLoopbackHttpServer server = new();
			Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
				LoginResponse() with { Delay = TimeSpan.FromMilliseconds(150) },
				new ScriptedResponse(Body: "download"));
			CreatioClient client = new(server.BaseUri.ToString(), "user", "password") { SkipPing = true };

			client.DownloadFileByGet(new Uri(server.BaseUri, "file").ToString(), path, requestTimeout: 50);

			(await capture).Should().HaveCount(2);
			(await File.ReadAllTextAsync(path)).Should().Be("download");
		}
		finally {
			File.Delete(path);
		}
	}

	[Test]
	public async Task ExecutePostRequest_ShouldRejectNullBodyBeforeSending()
	{
		await using ScriptedLoopbackHttpServer server = new();
		CreatioClient client = new(server.BaseUri.ToString(), "token");

		Action act = () => client.ExecutePostRequest(server.BaseUri.ToString(), null);

		act.Should().Throw<ArgumentNullException>();
	}

	[Test]
	public async Task Login_ShouldRetainWebExceptionContractForHttpErrors()
	{
		await using ScriptedLoopbackHttpServer server = new();
		Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
			new ScriptedResponse(StatusCode: 500, Body: "login-error"));
		CreatioClient client = new(server.BaseUri.ToString(), "user", "password");

		Action act = client.Login;

		act.Should().Throw<WebException>();
		await capture;
	}

	[Test]
	public async Task UploadAlmFile_ShouldRetainEmptyStringContractForTransportFailure()
	{
		string path = Path.GetTempFileName();
		await File.WriteAllTextAsync(path, "body");
		try {
			await using ScriptedLoopbackHttpServer server = new();
			Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
				new ScriptedResponse(CloseWithoutResponse: true));
			CreatioClient client = new(server.BaseUri.ToString(), "token");

			string result = client.UploadAlmFile(server.BaseUri.ToString(), path);

			result.Should().BeEmpty();
			await capture;
		}
		finally {
			File.Delete(path);
		}
	}

	[Test]
	public async Task EmptyFileFacades_ShouldReturnEmptyStrings()
	{
		string path = Path.GetTempFileName();
		try {
			using CreatioClient client = new("https://example.invalid", "token");

			client.UploadFile("https://example.invalid/upload", path).Should().BeEmpty();
			client.UploadStaticFile("https://example.invalid/upload?x=1", path, "folder").Should().BeEmpty();
			(await client.UploadAttachmentAsync(new Creatio.Client.Dto.FileUploadInfo {
				EntitySchemaName = "ContactFile",
				ColumnName = "Data",
				FilePath = path,
				ParentColumnName = "Contact",
				ParentColumnValue = Guid.NewGuid()
			})).Should().BeEmpty();
		}
		finally {
			File.Delete(path);
		}
	}

	[Test]
	public async Task UploadFile_ShouldIgnoreCallerChunkSizeInSynchronousWrapper()
	{
		string path = Path.GetTempFileName();
		await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
		try
		{
			await using ScriptedLoopbackHttpServer server = new();
			Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
				new ScriptedResponse(Body: "{\"success\":true}"));
			CreatioClient client = new(server.BaseUri.ToString(), "token");

			client.UploadFile(server.BaseUri.ToString(), path, chunkSize: 1);
			IReadOnlyList<CapturedRequest> requests = await capture;

			requests.Should().ContainSingle(
				because: "the 1.0.40 synchronous wrapper does not forward its chunkSize argument");
			requests[0].Headers["Content-Range"].Should().Be("bytes 0-2/3");
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Test]
	public async Task UploadStaticFile_ShouldPreserveMalformedFolderNameQueryBaseline()
	{
		string path = Path.Combine(Path.GetTempPath(), $"creatio-client-{Guid.NewGuid():N}.bin");
		await File.WriteAllBytesAsync(path, new byte[] { 1 });
		try
		{
			await using ScriptedLoopbackHttpServer server = new();
			Task<IReadOnlyList<CapturedRequest>> capture = server.CaptureAsync(
				new ScriptedResponse(Body: "{\"success\":true}"));
			CreatioClient client = new(server.BaseUri.ToString(), "token");

			client.UploadStaticFile(new Uri(server.BaseUri, "upload?x=1").ToString(), path, "target");
			CapturedRequest request = (await capture).Single();

			request.Target.Should().Contain("fileName=").And.Contain(".binfolderName=target",
				because: "1.0.40 omits the ampersand before folderName");
		}
		finally
		{
			File.Delete(path);
		}
	}

	private static ScriptedResponse LoginResponse() => new(
		Body: "{\"Code\":0}",
		Headers: new Dictionary<string, string[]> {
			["Set-Cookie"] = new[] {
				".ASPXAUTH=session-token; Path=/",
				"BPMCSRF=csrf-token; Path=/"
			}
		});
}
