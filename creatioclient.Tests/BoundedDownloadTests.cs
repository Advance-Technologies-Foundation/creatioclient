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
		// Arrange
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

	[Test]
	[Description("The size refusal is not retried: with three configured attempts an oversized success body is refused after a single GET.")]
	public async Task DownloadFileByGetBoundedAsync_ShouldNotRetryTheSizeRefusal_WhenSeveralAttemptsAreConfigured() {
		// Arrange — the ceiling is a CALLER contract, not a transport hiccup: another attempt re-downloads the
		// same oversized body and cannot succeed. Without the exclusion the retry filter swallows the refusal
		// and the caller sees a timeout instead, so the exclusion needs coverage that only multiple configured
		// attempts can give. The script serves ONE response, so a second connect stays pending and is visible.
		await using ScriptedLoopbackHttpServer server = new();
		string destination = NewDestinationPath();
		byte[] body = Enumerable.Repeat((byte)'x', Ceiling * 64).ToArray();
		Task<IReadOnlyList<CapturedRequest>> capture =
			server.CaptureAsync(new ScriptedResponse(StatusCode: 200, BodyBytes: body));
		using CreatioClient client = new(server.BaseUri.ToString(), "token");
		client.SetRetryPolicy(3, 0, RetryPolicy.Simple);

		try {
			// Act
			Func<Task> download = () => client.DownloadFileByGetBoundedAsync(
				server.BaseUri.ToString(), destination, Ceiling, requestTimeout: 3_000);

			// Assert
			await download.Should().ThrowAsync<CreatioResponseTooLargeException>(
				because: "a retried oversized body cannot succeed, so the refusal must leave the client immediately instead of being swallowed by the retry filter");
			IReadOnlyList<CapturedRequest> requests = await capture;
			requests.Should().HaveCount(1,
				because: "the script serves a single response, so this pins what the served GET was rather than how many were attempted");
			server.HasPendingConnection.Should().BeFalse(
				because: "a refusal folded into another attempt leaves a second connection queued against the scripted server");
		} finally {
			DeleteIfPresent(destination);
		}
	}

	[Test]
	[Description("No chunk past the ceiling ever reaches the destination: the bytes on disk are observed through a symlink that cleanup cannot remove.")]
	public async Task DownloadFileByGetBoundedAsync_ShouldNotWriteAnOverLimitChunkToTheDestination() {
		// Arrange — asserting ObservedBytes alone cannot see the ordering: moving the ceiling check AFTER
		// WriteAsync reports the same count and then deletes the file, so the over-limit bytes leave no trace.
		// The destination is a SYMLINK to a real target: FileMode.Create follows it and writes the target,
		// while the cleanup deletes only the link, so the bytes that actually reached disk stay observable.
		await using ScriptedLoopbackHttpServer server = new();
		string destination = NewDestinationPath();
		string target = NewDestinationPath();
		const long ceiling = 100_000;
		File.WriteAllBytes(target, Array.Empty<byte>());
		try {
			File.CreateSymbolicLink(destination, target);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			DeleteIfPresent(target);
			Assert.Ignore("Creating a symbolic link needs elevation or Developer Mode on this host.");
		}
		byte[] body = Enumerable.Repeat((byte)'x', 1024 * 1024).ToArray();
		Task<IReadOnlyList<CapturedRequest>> capture =
			server.CaptureAsync(new ScriptedResponse(StatusCode: 200, BodyBytes: body));
		using CreatioClient client = new(server.BaseUri.ToString(), "token");

		try {
			// Act
			Func<Task> download = () => client.DownloadFileByGetBoundedAsync(
				server.BaseUri.ToString(), destination, ceiling);

			// Assert
			await download.Should().ThrowAsync<CreatioResponseTooLargeException>(
				because: "the body is ten times the ceiling and must be refused");
			long onDisk = new FileInfo(target).Length;
			onDisk.Should().BeLessThanOrEqualTo(ceiling,
				because: "the ceiling is tested BEFORE each write, so a chunk that would cross it never reaches the destination - a check placed after the write would leave more than the ceiling on disk");
			onDisk.Should().BeGreaterThan(0,
				because: "the writes below the ceiling did happen, so the observation is of real destination bytes rather than an untouched file");
			await capture;
		} finally {
			DeleteIfPresent(destination);
			DeleteIfPresent(target);
		}
	}

	[Test]
	[Description("A zero ceiling refuses any body at all, so a caller whose remaining quota is zero is not served an unbounded download.")]
	public async Task DownloadFileByGetBoundedAsync_ShouldRefuseAnyBody_WhenTheCeilingIsZero() {
		// Arrange — zero is a legitimate ceiling ("accept nothing"), while -1 is the internal unbounded
		// sentinel. Nothing else pins that boundary: relaxing the ceiling test from >= 0 to > 0 turns a
		// requested zero into an unbounded transfer and leaves the rest of the suite green.
		await using ScriptedLoopbackHttpServer server = new();
		string destination = NewDestinationPath();
		Task<IReadOnlyList<CapturedRequest>> capture =
			server.CaptureAsync(new ScriptedResponse(StatusCode: 200, BodyBytes: new byte[] { (byte)'x' }));
		using CreatioClient client = new(server.BaseUri.ToString(), "token");

		try {
			// Act
			Func<Task> download = () => client.DownloadFileByGetBoundedAsync(
				server.BaseUri.ToString(), destination, 0);

			// Assert
			CreatioResponseTooLargeException failure =
				(await download.Should().ThrowAsync<CreatioResponseTooLargeException>(
					because: "a caller that asked to accept nothing must be given nothing, not everything")).Which;
			failure.MaxBytes.Should().Be(0,
				because: "the refused ceiling reported must be the zero the caller asked for");
			File.Exists(destination).Should().BeFalse(
				because: "a refused transfer leaves no file, and a zero ceiling refuses every non-empty body");
			await capture;
		} finally {
			DeleteIfPresent(destination);
		}
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
