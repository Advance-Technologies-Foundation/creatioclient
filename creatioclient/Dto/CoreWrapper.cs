using System.Collections.Generic;
using Newtonsoft.Json;

namespace Creatio.Client.Dto
{
	[JsonObject]
	public class CoreWrapper
	{

		#region Properties: Public

		[JsonProperty("arguments")]
		public IEnumerable<WsMessage> Arguments { get; set; }

		[JsonProperty("target")]
		public string Target { get; set; }

		[JsonProperty("type")]
		public int Type { get; set; }

		#endregion

	}
}