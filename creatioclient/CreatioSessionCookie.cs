using System;

namespace Creatio.Client
{
	/// <summary>
	/// Detached session-cookie data suitable for transferring a Creatio session to another HTTP or
	/// browser client. Cookie values are authentication secrets and must be protected.
	/// </summary>
	public sealed class CreatioSessionCookie
	{
		/// <summary>Initializes a detached session cookie.</summary>
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "S107:Methods should not have too many parameters",
			Justification = "The immutable transport mirrors the eight browser cookie fields as one explicit secret-bearing value.")]
		public CreatioSessionCookie(string name, string value, string domain, string path,
			bool httpOnly, bool secure, string sameSite, DateTime expires)
		{
			Name = name;
			Value = value;
			Domain = domain;
			Path = path;
			HttpOnly = httpOnly;
			Secure = secure;
			SameSite = sameSite;
			Expires = expires;
		}

		/// <summary>Cookie name.</summary>
		public string Name { get; }
		/// <summary>Cookie value.</summary>
		public string Value { get; }
		/// <summary>Cookie domain.</summary>
		public string Domain { get; }
		/// <summary>Cookie path.</summary>
		public string Path { get; }
		/// <summary>Whether browser script must be denied access to the cookie.</summary>
		public bool HttpOnly { get; }
		/// <summary>Whether the cookie is restricted to secure transport.</summary>
		public bool Secure { get; }
		/// <summary>Normalized browser SameSite value: Lax, Strict, or None.</summary>
		public string SameSite { get; }
		/// <summary>Cookie expiry, or <see cref="DateTime.MinValue"/> for a session cookie.</summary>
		public DateTime Expires { get; }
	}
}
