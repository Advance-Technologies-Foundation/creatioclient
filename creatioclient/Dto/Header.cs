using Newtonsoft.Json;

namespace Creatio.Client.Dto
{
	public class Header
	{

		#region Properties: Public

		[JsonProperty("BodyTypeName")]
		public string BodyTypeName { get; set; }

		[JsonProperty("Sender")]
		public string Sender { get; set; }

		#endregion

	}
}