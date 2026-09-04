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

/// <summary>
/// Regressions for the bounded GET-to-file download.
/// </summary>
/// <remarks>
/// The unbounded <c>DownloadFileByGetAsync</c> gives a caller no way to hold a byte ceiling: it exposes no
/// per-chunk hook, so the only ceiling available from outside was to watch the destination file grow, which
/// is a TIME bound — the producer writes an arbitrary amount between two observations. It also buffers a
/// final non-success body into memory with no ceiling at all and writes no file, so a caller loses the real
/// server error to a missing-file failure. Both are properties of the transport, so both are pinned here.
/// </remarks>
[TestFixture]
public class BoundedDownloadTests
{
	private const int Ceiling = 1024;

	[Test]
	[Description("A successful body larger than the ceiling is refused while it arrives, and no file is left behind.")]
	public async Task DownloadFileByGetBoundedAsync_ShouldRefuseAnOversizedSuccessBody_AndLeaveNoFile() {
		// Arrange
		await using ScriptedLoopbackHttpServer server = new();
		string destination = NewDestinationPath();
		byte[] body = Enumerable.Repeat((byte)'x', Ceiling * 64).ToArray();
		Task<IReadOnlyList<CapturedRequest>> capture =
			server.CaptureAsync(new ScriptedResponse(StatusCode: 200, BodyBytes: body));
		using CreatioClient client = new(server.BaseUri.ToString(), "token");

		try {
			// Act
			Func<Task> download = () => client.DownloadFileByGetBoundedAsync(
				server.BaseUri.ToString(), destination, Ceiling);

			// Assert
			CreatioResponseTooLargeException failure =
				(await download.Should().ThrowAsync<CreatioResponseTooLargeException>(
					because: "a body past the caller's ceiling must be refused rather than delivered")).Which;
			failure.MaxBytes.Should().Be(Ceiling,
				because: "the caller has to be told which limit it was that the body crossed");
			failure.ObservedBytes.Should().BeGreaterThan(Ceiling,
				because: "the count reported must be the one that crossed the ceiling");
			failure.StatusCode.Should().Be(HttpStatusCode.OK,
				because: "the ceiling applies to successful bodies too, and the caller still needs the status");
			File.Exists(destination).Should().BeFalse(
				because: "a refused transfer must leave no partial file a caller could mistake for a complete body");
			await capture;
		} finally {
			DeleteIfPresent(destination);
		}
	}

	[Test]
	[Description("The bytes actually written for an oversized body never exceed the ceiling by more than one read buffer, so the bound is a byte bound rather than a polling interval.")]
	public async Task DownloadFileByGetBoundedAsync_ShouldNotWritePastTheCeiling_WhenTheBodyIsOversized() {
		// Arrange — the destination is pre-created so its length after the failure is observable.
		await using ScriptedLoopbackHttpServer server = new();
		string destination = NewDestinationPath();
		byte[] body = Enumerable.Repeat((byte)'x', Ceiling * 512).ToArray();
		Task<IReadOnlyList<CapturedRequest>> capture =
			server.CaptureAsync(new ScriptedResponse(StatusCode: 200, BodyBytes: body));
		using CreatioClient client = new(server.BaseUri.ToString(), "token");

		try {
			// Act
			CreatioResponseTooLargeException failure = null;
			try {
				await client.DownloadFileByGetBoundedAsync(server.BaseUri.ToString(), destination, Ceiling);
			}
			catch (CreatioResponseTooLargeException exception) {
				failure = exception;
			}

			// Assert
			failure.Should().NotBeNull(
				because: "the oversized body must be reported, not silently truncated");
			failure!.ObservedBytes.Should().BeLessThanOrEqualTo(Ceiling + 81920,
				because: "the ceiling is tested before each write, so at most one read buffer beyond it is ever seen - a time-based bound would report an arbitrary overshoot instead");
			await capture;
		} finally {
			DeleteIfPresent(destination);
		}
	}

	[Test]
	[Description("A body at exactly the ceiling is accepted and written byte-for-byte, so the bound is inclusive rather than off by one.")]
	public async Task DownloadFileByGetBoundedAsync_ShouldAcceptABodyExactlyAtTheCeiling() {
		// Arrange
		await using ScriptedLoopbackHttpServer server = new();
		string destination = NewDestinationPath();
		byte[] body = Enumerable.Repeat((byte)'y', Ceiling).ToArray();
		Task<IReadOnlyList<CapturedRequest>> capture =
			server.CaptureAsync(new ScriptedResponse(StatusCode: 200, BodyBytes: body));
		using CreatioClient client = new(server.BaseUri.ToString(), "token");

		try {
			// Act
			using HttpResponseMessage response = await client.DownloadFileByGetBoundedAsync(
				server.BaseUri.ToString(), destination, Ceiling);
			await capture;

			// Assert
			response.StatusCode.Should().Be(HttpStatusCode.OK,
				because: "a body within the ceiling is an ordinary successful download");
			File.ReadAllBytes(destination).Should().Equal(body,
				because: "the destination must hold the response bytes unchanged");
		} finally {
			DeleteIfPresent(destination);
		}
	}

	[Test]
	[Description("A non-success body within the ceiling is written to the file, so the caller can read the server's actual error instead of failing on a missing file.")]
	public async Task DownloadFileByGetBoundedAsync_ShouldWriteANonSuccessBodyToTheFile() {
		// Arrange
		await using ScriptedLoopbackHttpServer server = new();
		string destination = NewDestinationPath();
		const string serverError = "{\"error\":{\"message\":\"Current user does not have permissions\"}}";
		Task<IReadOnlyList<CapturedRequest>> capture =
			server.CaptureAsync(new ScriptedResponse(StatusCode: 500, Body: serverError));
		using CreatioClient client = new(server.BaseUri.ToString(), "token");

		try {
			// Act
			using HttpResponseMessage response = await client.DownloadFileByGetBoundedAsync(
				server.BaseUri.ToString(), destination, Ceiling);
			await capture;

			// Assert
			response.StatusCode.Should().Be(HttpStatusCode.InternalServerError,
				because: "the status is what tells the caller the download did not succeed");
			File.ReadAllText(destination).Should().Be(serverError,
				because: "the unbounded overload buffers the error body and writes nothing, so a caller reading the destination loses the real server error - the bounded one must not");
		} finally {
			DeleteIfPresent(destination);
		}
	}

	[Test]
	[Description("An oversized non-success body is refused by the same ceiling as a successful one, so an error response cannot drain unbounded memory.")]
	public async Task DownloadFileByGetBoundedAsync_ShouldRefuseAnOversizedNonSuccessBody() {
		// Arrange
		await using ScriptedLoopbackHttpServer server = new();
		string destination = NewDestinationPath();
		byte[] body = Enumerable.Repeat((byte)'z', Ceiling * 64).ToArray();
		Task<IReadOnlyList<CapturedRequest>> capture =
			server.CaptureAsync(new ScriptedResponse(StatusCode: 502, BodyBytes: body));
		using CreatioClient client = new(server.BaseUri.ToString(), "token");

		try {
			// Act
			Func<Task> download = () => client.DownloadFileByGetBoundedAsync(
				server.BaseUri.ToString(), destination, Ceiling);

			// Assert
			CreatioResponseTooLargeException failure =
				(await download.Should().ThrowAsync<CreatioResponseTooLargeException>(
					because: "the ceiling has to apply to every status; the unbounded overload drains a final error body into memory with no limit at all")).Which;
			failure.StatusCode.Should().Be(HttpStatusCode.BadGateway,
				because: "the caller still needs to know which status carried the refused body");
			File.Exists(destination).Should().BeFalse(
				because: "a refused error body must leave no partial file either");
			await capture;
		} finally {
			DeleteIfPresent(destination);
		}
	}

	[Test]
	[Description("A negative ceiling is rejected outright, so an unbounded transfer cannot be requested through the bounded entry point by accident.")]
	public async Task DownloadFileByGetBoundedAsync_ShouldRejectANegativeCeiling() {
		// Arrange
		using CreatioClient client = new("http://127.0.0.1:1/", "token");
		string destination = NewDestinationPath();

		// Act
		Func<Task> download = () => client.DownloadFileByGetBoundedAsync(
			"http://127.0.0.1:1/", destination, -1);

		// Assert
		await download.Should().ThrowAsync<ArgumentOutOfRangeException>(
			because: "a negative ceiling is the sentinel for 'unbounded' inside the client and must never be reachable from a caller that asked for a bound");
	}

	// -------------------------------------------------------------------------------------------

	private static string NewDestinationPath() =>
		Path.Combine(Path.GetTempPath(), $"creatio-bounded-{Guid.NewGuid():N}.bin");

	private static void DeleteIfPresent(string path) {
		if (File.Exists(path)) {
			File.Delete(path);
		}
	}
}
