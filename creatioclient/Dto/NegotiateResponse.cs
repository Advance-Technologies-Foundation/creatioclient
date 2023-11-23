using Newtonsoft.Json;

namespace Creatio.Client.Dto
{
	[JsonObject]
	public class NegotiateResponse
	{

		#region Properties: Public

		[JsonProperty("connectionId")]
		public string ConnectionId { get; set; }

		[JsonProperty("connectionToken")]
		public string ConnectionToken { get; set; }

		[JsonProperty("negotiateVersion")]
		public int Version { get; set; }

		#endregion

	}
}