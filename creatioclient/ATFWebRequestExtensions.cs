using System;
using System.IO;
using System.Net;
using Creatio.Client.Dto;

namespace Creatio.Client
{
	public static class ATFWebRequestExtension
	{
		public static string GetServiceResponse(this HttpWebRequest request) {
			
			try {
				using (WebResponse response = request.GetResponse()) {
					using (var dataStream = response.GetResponseStream()) {
						using (StreamReader reader = new StreamReader(dataStream)) {
							return reader.ReadToEnd();
						}
					}
				}
			}
			catch (WebException webEx) {
				string errorContent = string.Empty;
				// Try to read the error response content
				if (webEx.Response != null) {
					using (var errorResponse = webEx.Response) {
						using (var dataStream = errorResponse.GetResponseStream()) {
							using (StreamReader reader = new StreamReader(dataStream)) {
								errorContent = reader.ReadToEnd();
							}
						}
					}
				}
				return errorContent;
			} 
			catch (Exception ex) {
				return "Error: " + ex.Message;
			}
		}

		public static void SaveToFile(this HttpWebRequest request, string filePath) {
			using (WebResponse response = request.GetResponse()) {
				using (var dataStream = response.GetResponseStream()) {
					if (dataStream != null) {
						using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write)) {
							dataStream.CopyTo(fileStream);
						}
					}
				}
			}
		}
	}
}
