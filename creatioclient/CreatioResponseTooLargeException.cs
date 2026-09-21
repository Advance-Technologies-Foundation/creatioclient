using System;
using System.Globalization;
using System.Net;

namespace Creatio.Client
{
	/// <summary>
	/// Thrown by the bounded download entry points when a response body reaches the caller's byte ceiling.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The ceiling is enforced INSIDE the copy loop: the count is checked before each write, so the
	/// destination file never exceeds <see cref="MaxBytes"/> and no more than one buffer beyond the ceiling
	/// is ever read off the socket. A caller that measured the destination after the transfer instead was
	/// applying a time bound, not a byte bound — the producer can write an arbitrary amount between two
	/// observations of the growing file.
	/// </para>
	/// <para>
	/// <see cref="StatusCode"/> is carried because the ceiling applies to error bodies as well as successful
	/// ones. A caller that must report what the server said still learns the status even when the body was
	/// refused, which a bare size error would not tell it.
	/// </para>
	/// </remarks>
	public sealed class CreatioResponseTooLargeException : Exception
	{
		/// <summary>
		/// Initializes a new instance of the <see cref="CreatioResponseTooLargeException"/> class.
		/// </summary>
		/// <param name="statusCode">Status of the response whose body crossed the ceiling.</param>
		/// <param name="observedBytes">Bytes counted when the ceiling was crossed.</param>
		/// <param name="maxBytes">The ceiling the caller asked for.</param>
		public CreatioResponseTooLargeException(HttpStatusCode statusCode, long observedBytes, long maxBytes)
			: base(string.Format(CultureInfo.InvariantCulture,
				"The response body exceeded the {0}-byte limit requested by the caller "
				+ "(at least {1} bytes, HTTP status {2}). The transfer was abandoned and no complete file was written.",
				maxBytes, observedBytes, (int)statusCode))
		{
			StatusCode = statusCode;
			ObservedBytes = observedBytes;
			MaxBytes = maxBytes;
		}

		/// <summary>Status of the response whose body crossed the ceiling.</summary>
		public HttpStatusCode StatusCode { get; }

		/// <summary>Bytes counted when the ceiling was crossed. At least <see cref="MaxBytes"/> plus one.</summary>
		public long ObservedBytes { get; }

		/// <summary>The ceiling the caller asked for.</summary>
		public long MaxBytes { get; }
	}
}
