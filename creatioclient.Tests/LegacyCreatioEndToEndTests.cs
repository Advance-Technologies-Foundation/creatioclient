using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Creatio.Client.Dto;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Creatio.Client.Tests;

[TestFixture]
[Category("E2E")]
[NonParallelizable]
public class LegacyCreatioEndToEndTests
{
	private string _appUrl;
	private string _userName;
	private string _password;
	private bool _isNetCore;

	[OneTimeSetUp]
	public void ReadEnvironment()
	{
		_appUrl = RequireEnvironmentVariable("CREATIO_URL").TrimEnd('/');
		_userName = RequireEnvironmentVariable("CREATIO_USERNAME");
		_password = RequireEnvironmentVariable("CREATIO_PASSWORD");
		_isNetCore = bool.Parse(RequireEnvironmentVariable("CREATIO_IS_NETCORE"));
	}

	[Test]
	public void PasswordLogin_AndAuthenticatedGet_ShouldReachCreatio()
	{
		CreatioClient client = CreateClient();

		client.Login();
		string response = client.ExecuteGetRequest($"{_appUrl}/0/ping");

		response.Should().NotBeNull();
	}

	[Test]
	public void ODataCrud_ShouldPreserveLegacySynchronousBehavior()
	{
		CreatioClient client = CreateClient();
		Guid contactId = Guid.NewGuid();
		string initialName = $"CreatioClient E2E {contactId:N}";
		string updatedName = $"{initialName} updated";
		string contactUrl = $"{_appUrl}/odata/Contact({contactId:D})";
		bool created = false;
		try
		{
			string createResponse = client.ExecutePostRequest(
				$"{_appUrl}/odata/Contact",
				new JObject { ["Id"] = contactId, ["Name"] = initialName }.ToString());
			created = true;

			JObject.Parse(createResponse)["Name"]!.Value<string>().Should().Be(initialName);
			client.ExecuteGetRequest($"{contactUrl}?$select=Id,Name")
				.Should().Contain(initialName);

			client.ExecutePatchRequest(contactUrl, new JObject { ["Name"] = updatedName }.ToString())
				.Should().BeEmpty();
			client.ExecuteGetRequest($"{contactUrl}?$select=Id,Name")
				.Should().Contain(updatedName);

			client.ExecuteDeleteRequest(contactUrl, string.Empty).Should().BeEmpty();
			created = false;
		}
		finally
		{
			if (created) {
				client.ExecuteDeleteRequest(contactUrl, string.Empty);
			}
		}
	}

	[Test]
	public async Task AttachmentUploadAndDownload_ShouldRoundTripBinaryContent()
	{
		CreatioClient client = CreateClient();
		Guid contactId = Guid.NewGuid();
		string fileName = $"creatioclient-e2e-{Guid.NewGuid():N}.png";
		string contactUrl = $"{_appUrl}/odata/Contact({contactId:D})";
		string sourcePath = Path.Combine(Path.GetTempPath(), fileName);
		string downloadPath = Path.Combine(Path.GetTempPath(), $"download-{fileName}");
		byte[] pngHeader = Convert.FromBase64String(
			"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
		byte[] expected = pngHeader.Concat(Enumerable.Range(0, 4097)
			.Select(index => (byte)(index % 251))).ToArray();
		bool contactCreated = false;
		try
		{
			await File.WriteAllBytesAsync(sourcePath, expected);
			client.ExecutePostRequest(
				$"{_appUrl}/odata/Contact",
				new JObject { ["Id"] = contactId, ["Name"] = $"CreatioClient file E2E {contactId:N}" }.ToString());
			contactCreated = true;

			string uploadResponse = await client.UploadAttachmentAsync(new FileUploadInfo {
				EntitySchemaName = "ContactFile",
				ColumnName = "Data",
				FilePath = sourcePath,
				ParentColumnName = "Contact",
				ParentColumnValue = contactId
			}, chunkSize: 1024);
			JObject.Parse(uploadResponse)["success"]!.Value<bool>().Should().BeTrue();

			string filter = Uri.EscapeDataString($"Name eq '{fileName}'");
			string queryResponse = client.ExecuteGetRequest(
				$"{_appUrl}/odata/ContactFile?$select=Id&$filter={filter}");
			Guid fileId = Guid.Parse(JObject.Parse(queryResponse)["value"]!.Single()!["Id"]!.Value<string>()!);

			client.DownloadAttachment("ContactFile", fileId, downloadPath).Should().BeTrue();
			File.ReadAllBytes(downloadPath).Should().Equal(expected);
		}
		finally
		{
			if (contactCreated) {
				client.ExecuteDeleteRequest(contactUrl, string.Empty);
			}
			if (File.Exists(sourcePath)) {
				File.Delete(sourcePath);
			}
			if (File.Exists(downloadPath)) {
				File.Delete(downloadPath);
			}
		}
	}

	[Test]
	public async Task AsyncRequests_ShouldExposeResponsesAndSupportConcurrentUse()
	{
		using CreatioClient client = CreateClient();
		Task<HttpResponseMessage>[] requests = Enumerable.Range(0, 5)
			.Select(_ => client.ExecuteGetRequestAsync($"{_appUrl}/odata/Contact?$select=Id&$top=1"))
			.ToArray();

		HttpResponseMessage[] responses = await Task.WhenAll(requests);
		try {
			responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
			string[] bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));
			bodies.Should().OnlyContain(body => body.Contains("\"value\""));
		} finally {
			foreach (HttpResponseMessage response in responses) {
				response.Dispose();
			}
		}
	}

	private CreatioClient CreateClient() => new(_appUrl, _userName, _password, _isNetCore);

	private static string RequireEnvironmentVariable(string name)
	{
		string value = Environment.GetEnvironmentVariable(name);
		if (string.IsNullOrWhiteSpace(value)) {
			Assert.Ignore($"Set {name} to run Creatio end-to-end tests.");
		}
		return value;
	}
}
