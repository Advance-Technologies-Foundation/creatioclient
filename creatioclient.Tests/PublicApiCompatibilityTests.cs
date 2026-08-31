using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Creatio.Client.Dto;
using FluentAssertions;
using NUnit.Framework;

namespace Creatio.Client.Tests;

[TestFixture]
[Category("CompatibilityBaseline")]
public class PublicApiCompatibilityTests
{
	[Test]
	public void Package_ShouldRetainAllVersion1040PublicTypes()
	{
		string[] expected = {
			"Creatio.Client.ATFWebRequestExtension",
			"Creatio.Client.CreatioClient",
			"Creatio.Client.ICreatioClient",
			"Creatio.Client.RetryPolicy",
			"Creatio.Client.Dto.ErrorInfo",
			"Creatio.Client.Dto.FileUploadInfo",
			"Creatio.Client.Dto.FileUploadResponseDto",
			"Creatio.Client.Dto.Header",
			"Creatio.Client.Dto.NegotiateResponse",
			"Creatio.Client.Dto.SignalRWrapper",
			"Creatio.Client.Dto.TokenResponse",
			"Creatio.Client.Dto.WsMessage"
		};

		HashSet<string> actual = typeof(CreatioClient).Assembly.GetExportedTypes()
			.Select(type => type.FullName!)
			.ToHashSet(StringComparer.Ordinal);

		actual.Should().Contain(expected,
			because: "every type exported by Creatio.Client 1.0.40 is part of the binary compatibility baseline");
	}

	[Test]
	public void CreatioClient_ShouldRetainAllVersion1040ConstructorsAndDefaults()
	{
		string[] expected = {
			"System.String appUrl, System.String bearerToken, System.Boolean isNetCore=False",
			"System.String appUrl, System.String userName, System.String userPassword, System.Boolean isNetCore=False",
			"System.String appUrl, System.String userName, System.String userPassword, System.Boolean useUntrustedSsl, System.Boolean isNetCore=False",
			"System.String appUrl, System.String userName, System.String userPassword, System.Int32 timeZoneOffset, System.Boolean isNetCore=False",
			"System.String appUrl, System.Boolean useUntrustedSsl, System.Net.ICredentials credentials, System.Boolean isNetCore=False"
		};

		string[] actual = typeof(CreatioClient).GetConstructors()
			.Select(constructor => FormatParameters(constructor.GetParameters()))
			.ToArray();

		actual.Should().Contain(expected,
			because: "constructor parameter order and optional defaults are source and binary compatibility contracts");
	}

	[Test]
	public void ICreatioClient_ShouldRetainAllVersion1040MembersAndDefaults()
	{
		string[] expected = {
			"System.String CallConfigurationService(System.String serviceName, System.String serviceMethod, System.String requestData, System.Int32 requestTimeout=100000)",
			"System.Boolean DownloadAttachment(System.String schemaName, System.Guid recordId, System.String filePath, System.Int32 timeout=100000)",
			"System.Void DownloadFile(System.String url, System.String filePath, System.String requestData, System.Int32 requestTimeout=100000)",
			"System.Void DownloadFileByGet(System.String url, System.String filePath, System.Int32 requestTimeout=100000)",
			"System.String ExecuteGetRequest(System.String url, System.Int32 requestTimeout=100000, System.Int32 maxAttempts=1, System.Int32 delaySec=1)",
			"System.String ExecutePatchRequest(System.String url, System.String requestData, System.Int32 requestTimeout=100000, System.Int32 maxAttempts=1, System.Int32 delaySec=1)",
			"System.String ExecutePostRequest(System.String url, System.String requestData, System.Int32 requestTimeout=100000, System.Int32 maxAttempts=1, System.Int32 delaySec=1)",
			"System.String ExecutePutRequest(System.String url, System.String requestData, System.Int32 requestTimeout=100000, System.Int32 maxAttempts=1, System.Int32 delaySec=1)",
			"System.Void Login()",
			"System.Void SetRetryPolicy(System.Int32 maxAttempts, System.Int32 delaySec, Creatio.Client.RetryPolicy retryPolicy)",
			"System.Void StartListening(System.Threading.CancellationToken cancellationToken)",
			"System.String UploadAlmFile(System.String url, System.String filePath)",
			"System.Threading.Tasks.Task<System.String> UploadAttachmentAsync(Creatio.Client.Dto.FileUploadInfo uploadInfo, System.Int32 timeout=100000, System.Int32 chunkSize=1048576)",
			"System.String UploadFile(System.String url, System.String filePath, System.Int32 requestTimeout=100000, System.Int32 chunkSize=1048576)",
			"System.Threading.Tasks.Task<System.String> UploadFileAsync(System.String url, System.String filePath, System.Int32 defaultTimeout=100000, System.Int32 chunkSize=1048576)"
		};

		string[] actual = typeof(ICreatioClient).GetMethods()
			.Where(method => !method.IsSpecialName)
			.Select(FormatMethod)
			.ToArray();

		actual.Should().Contain(expected,
			because: "all Creatio.Client 1.0.40 interface methods must remain callable with their existing defaults");
		typeof(ICreatioClient).GetEvents().Select(@event => @event.Name)
			.Should().Contain(new[] { "ConnectionStateChanged", "MessageReceived" });
	}

	[Test]
	public void IAsyncCreatioClient_ShouldAddCancellationAwareResponsesWithoutChangingLegacyInterface()
	{
		string[] expected = {
			"CallConfigurationServiceAsync",
			"DownloadAttachmentAsync",
			"DownloadFileAsync",
			"DownloadFileByGetAsync",
			"ExecuteDeleteRequestAsync",
			"ExecuteGetRequestAsync",
			"ExecutePatchRequestAsync",
			"ExecutePostRequestAsync",
			"ExecutePutRequestAsync",
			"LoginAsync",
			"UploadAlmFileAsync",
			"UploadAlmFileByChunkAsync",
			"UploadAttachmentResponseAsync",
			"UploadChunkAlmFileAsync",
			"UploadFileAsync",
			"UploadFile_originalAsync",
			"UploadStaticFileAsync"
		};

		MethodInfo[] methods = typeof(IAsyncCreatioClient).GetMethods()
			.Where(method => expected.Contains(method.Name)
				&& method.ReturnType == typeof(Task<HttpResponseMessage>))
			.ToArray();

		methods.Select(method => method.Name).Should().Contain(expected);
		methods.Should().OnlyContain(method => method.ReturnType == typeof(Task<HttpResponseMessage>));
		methods.Should().OnlyContain(method => method.GetParameters().Last().ParameterType == typeof(CancellationToken));
		typeof(ICreatioClient).GetMethods().Should().NotContain(
			method => method.ReturnType == typeof(Task<HttpResponseMessage>),
			because: "adding abstract members to the established interface would break existing implementers");
	}

	[Test]
	public void CreatioClient_ShouldRetainConcreteOnlyVersion1040Members()
	{
		string[] expected = {
			"Creatio.Client.CreatioClient CreateOAuth20Client(System.String app, System.String authApp, System.String clientId, System.String clientSecret, System.Boolean isNetCore=False)",
			"System.String ExecuteDeleteRequest(System.String url, System.String requestData, System.Int32 requestTimeout=10000, System.Int32 maxAttempts=1, System.Int32 delaySec=1)",
			"System.Void Login(System.Int32 requestTimeout)",
			"System.String UploadAlmFileByChunk(System.String url, System.String filePath)",
			"System.String UploadChunkAlmFile(System.String url, System.Byte[] data, System.Int32 downloadedSize, System.Int32 totalSize)",
			"System.String UploadFile_original(System.String url, System.String filePath, System.Int32 defaultTimeout=100000)",
			"System.String UploadStaticFile(System.String url, System.String filePath, System.String folderName, System.Int32 defaultTimeout=100000, System.Int32 chunkSize=1048576)",
			"System.Threading.Tasks.Task<System.String> UploadStaticFileAsync(System.String url, System.String filePath, System.String folderName, System.Int32 defaultTimeout=100000, System.Int32 chunkSize=31457280)"
		};

		BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
		string[] actual = typeof(CreatioClient).GetMethods(flags)
			.Where(method => !method.IsSpecialName)
			.Select(FormatMethod)
			.ToArray();

		actual.Should().Contain(expected,
			because: "concrete-only members are still public API even when absent from ICreatioClient");
		typeof(CreatioClient).GetMethod(nameof(CreatioClient.ExportSessionCookies), Type.EmptyTypes).Should()
			.NotBeNull(because: "browser integrations need cookie export without changing an established interface");
		typeof(CreatioClient).GetMethod(nameof(CreatioClient.ImportSessionCookies),
			new[] { typeof(IEnumerable<CreatioSessionCookie>) }).Should().NotBeNull(
			because: "cached sessions must be reusable without changing an established interface");
		typeof(CreatioClient).GetMethod(nameof(CreatioClient.UploadImageAsync),
			new[] { typeof(string), typeof(byte[]), typeof(string), typeof(string), typeof(int),
				typeof(CancellationToken) }).Should().NotBeNull(
			because: "the Image API needs a response-returning binary operation without changing an established interface");
		typeof(CreatioClient).GetProperty(nameof(CreatioClient.SkipPing)).Should().NotBeNull();
		typeof(CreatioClient).GetProperty(nameof(CreatioClient.TimeZoneOffset)).Should().NotBeNull();
		typeof(CreatioClient).GetMethod("OnMessageReceived", BindingFlags.Instance | BindingFlags.NonPublic)!
			.IsFamily.Should().BeTrue(because: "the protected virtual notification seam is inheritable API");
	}

	[Test]
	public void Dtos_ShouldRetainVersion1040PublicProperties()
	{
		AssertProperties<FileUploadInfo>("AdditionalParams", "ColumnName", "EntitySchemaName", "FilePath",
			"ParentColumnName", "ParentColumnValue");
		AssertProperties<FileUploadResponseDto>("ErrorInfo", "Success");
		AssertProperties<ErrorInfo>("ErrorCode", "Message", "StackTrace");
		AssertProperties<Header>("BodyTypeName", "Sender");
		AssertProperties<NegotiateResponse>("ConnectionId", "ConnectionToken", "Version");
		AssertProperties<SignalRWrapper>("Arguments", "Target", "Type");
		AssertProperties<TokenResponse>("AccessToken", "ExpiresIn", "TokenType");
		AssertProperties<WsMessage>("Body", "Header", "Id");
	}

	[Test]
	public void SessionTransferApi_ShouldExposeCurrentCookieShapeAndStrictTlsBearerConstructor()
	{
		typeof(CreatioClient).Assembly.GetExportedTypes().Should().Contain(typeof(CreatioSessionCookie),
			because: "browser and service consumers need the detached public session contract");
		typeof(CreatioClient).GetConstructors().Select(constructor => FormatParameters(constructor.GetParameters()))
			.Should().Contain("System.String appUrl, System.String bearerToken, System.Boolean useUntrustedSsl, System.Boolean isNetCore",
				because: "bearer consumers must be able to enforce strict certificate validation explicitly");
		AssertProperties<CreatioSessionCookie>("Name", "Value", "Domain", "Path", "HttpOnly", "Secure",
			"SameSite", "Expires");
	}

	[Test]
	public void HttpWebRequestExtensions_ShouldRemainPublicCompatibilitySurface()
	{
		typeof(ATFWebRequestExtension).GetMethod(nameof(ATFWebRequestExtension.GetServiceResponse),
			new[] { typeof(HttpWebRequest) }).Should().NotBeNull();
		typeof(ATFWebRequestExtension).GetMethod(nameof(ATFWebRequestExtension.SaveToFile),
			new[] { typeof(HttpWebRequest), typeof(string) }).Should().NotBeNull();
	}

	private static void AssertProperties<T>(params string[] expected) =>
		typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
			.Select(property => property.Name)
			.Should().Contain(expected);

	private static string FormatMethod(MethodInfo method) =>
		$"{FormatType(method.ReturnType)} {method.Name}({FormatParameters(method.GetParameters())})";

	private static string FormatParameters(IEnumerable<ParameterInfo> parameters) =>
		string.Join(", ", parameters.Select(parameter =>
			$"{FormatType(parameter.ParameterType)} {parameter.Name}{FormatDefault(parameter)}"));

	private static string FormatDefault(ParameterInfo parameter)
	{
		if (!parameter.IsOptional) {
			return string.Empty;
		}
		if (parameter.DefaultValue is bool boolean) {
			return $"={boolean}";
		}
		return $"={parameter.DefaultValue}";
	}

	private static string FormatType(Type type)
	{
		if (type.IsGenericType) {
			string name = type.GetGenericTypeDefinition().FullName!.Split('`')[0];
			return $"{name}<{string.Join(",", type.GetGenericArguments().Select(FormatType))}>";
		}
		return type.FullName!;
	}
}
