using System;
using Newtonsoft.Json;

namespace Creatio.Client.Dto
{
	[JsonObject]
	public class WsMessage
	{

		#region Properties: Public

		[JsonProperty("Body")]
		public string Body { get; set; }

		[JsonProperty("Header")]
		public Header Header { get; set; }

		[JsonProperty("Id")]
		public Guid Id { get; set; }

		#endregion

	}
}