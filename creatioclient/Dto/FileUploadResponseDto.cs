using Newtonsoft.Json;

namespace Creatio.Client.Dto {
	
	
	public class FileUploadResponseDto
	{
		[JsonProperty("success")]
	    public bool Success { get; set; }
		
		[JsonProperty("errorInfo")]
	    public ErrorInfo ErrorInfo { get; set; }
	}
	
	public class ErrorInfo
	{
		[JsonProperty("errorCode")]
		public string ErrorCode { get; set; }
		
		[JsonProperty("message")]
		public string Message { get; set; }
		
		[JsonProperty("stackTrace")]
		public object StackTrace { get; set; }
	}	
}





