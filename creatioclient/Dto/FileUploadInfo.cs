namespace Creatio.Client.Dto
{

	using System;
	using System.Collections.Generic;

	#region Class: FileUploadInfo


	/// <summary>
	/// Represents information required to upload a file to a Creatio entity, including schema,
	/// column, file path, parent reference, and additional parameters.
	/// </summary>
	public class FileUploadInfo
	{

		#region Properties: Public

		/// <summary>
		/// File entity schema name.
		/// </summary>
		public string EntitySchemaName { get; set; }

		/// <summary>
		/// Data entity schema column name.
		/// </summary>
		public string ColumnName { get; set; }

		/// <summary>
		/// File path in.
		/// </summary>
		public string FilePath { get; set; }
		
		/// <summary>
		/// Parent column name.
		/// </summary>
		public string ParentColumnName { get; set; }

		/// <summary>
		/// Parent column value.
		/// </summary>
		public Guid ParentColumnValue { get; set; }

		/// <summary>
		/// Additional parameters for the file upload request.
		/// This can include any extra data that needs to be sent along with the file upload
		/// request, such as metadata or configuration options.
		/// The keys and values in this dictionary should be strings.
		/// </summary>
		public Dictionary<string, string> AdditionalParams { get; set; }

		#endregion

	}

	#endregion

}