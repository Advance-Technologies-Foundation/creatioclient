using Newtonsoft.Json;

namespace Creatio.Client.Dto
{
	public class TokenResponse
	{

		#region Properties: Public

		[JsonProperty("access_token")]
		public string AccessToken { get; set; }

		[JsonProperty("expires_in")]
		public int ExpiresIn { get; set; }

		[JsonProperty("token_type")]
		public string TokenType { get; set; }

		#endregion

	}
}