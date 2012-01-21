//-----------------------------------------------------------------------
// <copyright file="Smuggler.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Raven.Json.Linq;
using Raven.Client.Document;
using Raven.Client.Extensions;
using Raven.Client.Connection;
using Raven.Client;

namespace Raven.Smuggler
{
    using System.Configuration;

    public class Smuggler
	{
		static void Main(string[] args)
		{
            string url = ConfigurationManager.AppSettings["RavenConnectionString"];
            if (args.Length > 1)
            {
                url = args[1];
            }
            string operation = "out";
            if (args.Length > 0)
            {
                operation = args[0];
            }
            
            var documentStore = new DocumentStore { Url = url };
            documentStore.Initialize();
            try
            {
                switch (operation)
                {
                    case "in":
                        foreach (var backup in Directory.EnumerateFiles(AppDomain.CurrentDomain.SetupInformation.ApplicationBase, "*.dump"))
                        {
                            var database = Path.GetFileNameWithoutExtension(backup);
                            documentStore.DatabaseCommands.EnsureDatabaseExists(database);
                            var instanceUrl = url.Databases() + database + "/";
                            ImportData(instanceUrl, backup);
                        }
                        break;
                    case "out":

                        List<string> databases;
                        using (IDocumentSession session = documentStore.OpenSession())
                        {
                            databases = session.Advanced.DatabaseCommands.GetDatabaseNames().ToList();
                        }

                        foreach (string database in databases)
                        {
                            string databaseFileName = database + ".dump";
                            string instanceUrl = url.Databases() + database + "/";
                            bool exportIndexesOnly = false;
                            bool inlcudeAttachments = true;
                            ExportData(new ExportSpec(instanceUrl, databaseFileName, exportIndexesOnly,
                                                      inlcudeAttachments));
                        }

                        break;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Environment.Exit(-1);
            }
            finally
            {
                documentStore.Dispose();
            }
		}

		public class ExportSpec
		{
			public ExportSpec(string instanceUrl, string file, bool exportIndexesOnly, bool includeAttachments)
			{
				InstanceUrl = instanceUrl;
				File = file;
				ExportIndexesOnly = exportIndexesOnly;
				IncludeAttachments = includeAttachments;
			}

			public string InstanceUrl { get; private set; }

			public string File { get; private set; }

			public bool ExportIndexesOnly { get; private set; }

			public bool IncludeAttachments { get; private set; }
		}

		public static void ExportData(ExportSpec exportSpec)
		{
			using (var streamWriter = new StreamWriter(new GZipStream(File.Create(exportSpec.File), CompressionMode.Compress)))
			{
				var jsonWriter = new JsonTextWriter(streamWriter)
				{
					Formatting = Formatting.Indented
				};
				jsonWriter.WriteStartObject();
				jsonWriter.WritePropertyName("Indexes");
				jsonWriter.WriteStartArray();
				using (var webClient = new WebClient())
				{
					webClient.UseDefaultCredentials = true;
					webClient.Credentials = CredentialCache.DefaultNetworkCredentials;

					int totalCount = 0;
					while (true)
					{
						var documents = GetString(webClient.DownloadData(exportSpec.InstanceUrl + "indexes?pageSize=128&start=" + totalCount));
						var array = RavenJArray.Parse(documents);
						if (array.Length == 0)
						{
							Console.WriteLine("Done with reading indexes, total: {0}", totalCount);
							break;
						}
						totalCount += array.Length;
						Console.WriteLine("Reading batch of {0,3} indexes, read so far: {1,10:#,#}", array.Length,
										  totalCount);
						foreach (RavenJToken item in array)
						{
							item.WriteTo(jsonWriter);
						}
					}
				}
				jsonWriter.WriteEndArray();
				jsonWriter.WritePropertyName("Docs");
				jsonWriter.WriteStartArray();

				if (!exportSpec.ExportIndexesOnly)
				{
					ExportDocuments(exportSpec, jsonWriter);
				}
				jsonWriter.WriteEndArray();

				jsonWriter.WritePropertyName("Attachments");
				jsonWriter.WriteStartArray();
				if (exportSpec.IncludeAttachments)
				{
					ExportAttachments(jsonWriter, exportSpec);
				}
				jsonWriter.WriteEndArray();

				jsonWriter.WriteEndObject();
				streamWriter.Flush();
			}



		}

		private static void ExportDocuments(ExportSpec exportSpec, JsonTextWriter jsonWriter)
		{
			using (var webClient = new WebClient())
			{
				webClient.UseDefaultCredentials = true;
				webClient.Credentials = CredentialCache.DefaultNetworkCredentials;

				var lastEtag = Guid.Empty;
				int totalCount = 0;
				while (true)
				{
					var documents =
						GetString(webClient.DownloadData(exportSpec.InstanceUrl + "docs?pageSize=128&etag=" + lastEtag));
					var array = RavenJArray.Parse(documents);
					if (array.Length == 0)
					{
						Console.WriteLine("Done with reading documents, total: {0}", totalCount);
						break;
					}
					totalCount += array.Length;
					Console.WriteLine("Reading batch of {0,3} documents, read so far: {1,10:#,#}", array.Length,
									  totalCount);
					foreach (RavenJToken item in array)
					{
						item.WriteTo(jsonWriter);
					}
					lastEtag = new Guid(array.Last().Value<RavenJObject>("@metadata").Value<string>("@etag"));
				}
			}
		}

		static void ExportAttachments(JsonTextWriter jsonWriter, ExportSpec exportSpec)
		{
			using (var webClient = new WebClient())
			{
				webClient.UseDefaultCredentials = true;
				webClient.Credentials = CredentialCache.DefaultNetworkCredentials;

				var lastEtag = Guid.Empty;
				int totalCount = 0;
				while (true)
				{
					var attachmentInfo = GetString(webClient.DownloadData(exportSpec.InstanceUrl + "/static/?pageSize=128&etag=" + lastEtag));
					var array = RavenJArray.Parse(attachmentInfo);

					if (array.Length == 0)
					{
						Console.WriteLine("Done with reading attachments, total: {0}", totalCount);
						break;
					}

					totalCount += array.Length;
					Console.WriteLine("Reading batch of {0,3} attachments, read so far: {1,10:#,#}", array.Length,
									  totalCount);
					foreach (var item in array)
					{
						Console.WriteLine("Downloading attachment: {0}", item.Value<string>("Key"));
						var attachmentData = webClient.DownloadData(exportSpec.InstanceUrl + "/static/" + item.Value<string>("Key"));

						new RavenJObject
						{
							{"Data", attachmentData},
							{"Metadata", item.Value<RavenJObject>("Metadata")},
							{"Key", item.Value<string>("Key")}
						}
						.WriteTo(jsonWriter);
					}

					lastEtag = new Guid(array.Last().Value<string>("Etag"));
				}
			}
		}

		private class AttachmentExportInfo
		{
			public byte[] Data { get; set; }
			public RavenJObject Metadata { get; set; }
			public string Key { get; set; }
		}

		public static string GetString(byte[] downloadData)
		{
			var ms = new MemoryStream(downloadData);
			return new StreamReader(ms, Encoding.UTF8).ReadToEnd();
		}

		public static void ImportData(string instanceUrl, string file)
		{
			using (FileStream fileStream = File.OpenRead(file))
			{
				ImportData(fileStream, instanceUrl);
			}
		}

		public static void ImportData(Stream stream, string instanceUrl)
		{
			var sw = Stopwatch.StartNew();
			// Try to read the stream compressed, otherwise continue uncompressed.
			JsonTextReader jsonReader;
			try
			{
				var streamReader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress));

				jsonReader = new JsonTextReader(streamReader);

				if (jsonReader.Read() == false)
					return;
			}
			catch (InvalidDataException)
			{
				stream.Seek(0, SeekOrigin.Begin);

				StreamReader streamReader = new StreamReader(stream);

				jsonReader = new JsonTextReader(streamReader);

				if (jsonReader.Read() == false)
					return;
			}

			if (jsonReader.TokenType != JsonToken.StartObject)
				throw new InvalidDataException("StartObject was expected");

			// should read indexes now
			if (jsonReader.Read() == false)
				return;
			if (jsonReader.TokenType != JsonToken.PropertyName)
				throw new InvalidDataException("PropertyName was expected");
			if (Equals("Indexes", jsonReader.Value) == false)
				throw new InvalidDataException("Indexes property was expected");
			if (jsonReader.Read() == false)
				return;
			if (jsonReader.TokenType != JsonToken.StartArray)
				throw new InvalidDataException("StartArray was expected");
			using (var webClient = new WebClient())
			{
				webClient.UseDefaultCredentials = true;
				webClient.Headers.Add("Content-Type", "application/json; charset=utf-8");
				webClient.Credentials = CredentialCache.DefaultNetworkCredentials;
				while (jsonReader.Read() && jsonReader.TokenType != JsonToken.EndArray)
				{
					var index = RavenJToken.ReadFrom(jsonReader);
					var indexName = index.Value<string>("name");
					if (indexName.StartsWith("Raven/") || indexName.StartsWith("Temp/"))
						continue;
					using (var streamWriter = new StreamWriter(webClient.OpenWrite(instanceUrl + "indexes/" + indexName, "PUT")))
					using (var jsonTextWriter = new JsonTextWriter(streamWriter))
					{
						index.Value<RavenJObject>("definition").WriteTo(jsonTextWriter);
						jsonTextWriter.Flush();
						streamWriter.Flush();
					}
				}
			}
			// should read documents now
			if (jsonReader.Read() == false)
				return;
			if (jsonReader.TokenType != JsonToken.PropertyName)
				throw new InvalidDataException("PropertyName was expected");
			if (Equals("Docs", jsonReader.Value) == false)
				throw new InvalidDataException("Docs property was expected");
			if (jsonReader.Read() == false)
				return;
			if (jsonReader.TokenType != JsonToken.StartArray)
				throw new InvalidDataException("StartArray was expected");
			var batch = new List<RavenJObject>();
			int totalCount = 0;
			while (jsonReader.Read() && jsonReader.TokenType != JsonToken.EndArray)
			{
				totalCount += 1;
				var document = RavenJToken.ReadFrom(jsonReader);
				batch.Add((RavenJObject)document);
				if (batch.Count >= 128)
					FlushBatch(instanceUrl, batch);
			}
			FlushBatch(instanceUrl, batch);

			var attachmentCount = 0;
			if (jsonReader.Read() == false || jsonReader.TokenType == JsonToken.EndObject)
				return;
			if (jsonReader.TokenType != JsonToken.PropertyName)
				throw new InvalidDataException("PropertyName was expected");
			if (Equals("Attachments", jsonReader.Value) == false)
				throw new InvalidDataException("Attachment property was expected");
			if (jsonReader.Read() == false)
				return;
			if (jsonReader.TokenType != JsonToken.StartArray)
				throw new InvalidDataException("StartArray was expected");
			while (jsonReader.Read() && jsonReader.TokenType != JsonToken.EndArray)
			{
				using (var client = new WebClient())
				{
					attachmentCount += 1;
					var item = RavenJToken.ReadFrom(jsonReader);

					var attachmentExportInfo =
						new JsonSerializer
						{
							Converters = {new TrivialJsonToJsonJsonConverter()}
						}.Deserialize<AttachmentExportInfo>(new RavenJTokenReader(item));
					Console.WriteLine("Importing attachment {0}", attachmentExportInfo.Key);
					if (attachmentExportInfo.Metadata != null)
					{
						foreach (var header in attachmentExportInfo.Metadata)
						{
							client.Headers.Add(header.Key, StripQuotesIfNeeded(header.Value));
						}
					}

					using (var writer = client.OpenWrite(instanceUrl + "static/" + attachmentExportInfo.Key, "PUT"))
					{
						writer.Write(attachmentExportInfo.Data, 0, attachmentExportInfo.Data.Length);
						writer.Flush();
					}
				}
			}
			Console.WriteLine("Imported {0:#,#} documents and {1:#,#} attachments in {2:#,#} ms", totalCount, attachmentCount, sw.ElapsedMilliseconds);
		}

		public class TrivialJsonToJsonJsonConverter : JsonConverter
		{
			public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
			{
				throw new NotImplementedException();
			}

			public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
			{
				return RavenJObject.Load(reader);
			}

			public override bool CanConvert(Type objectType)
			{
				return objectType == typeof (RavenJObject);
			}
		}

		private static string StripQuotesIfNeeded(RavenJToken value)
		{
			var str = value.ToString(Formatting.None);
			if (str.StartsWith("\"") && str.EndsWith("\""))
				return str.Substring(1, str.Length - 2);
			return str;
		}

		private static void FlushBatch(string instanceUrl, List<RavenJObject> batch)
		{
			var sw = Stopwatch.StartNew();
			long size;
			using (var webClient = new WebClient())
			{
				webClient.Headers.Add("Content-Type", "application/json; charset=utf-8");
				webClient.UseDefaultCredentials = true;
				webClient.Credentials = CredentialCache.DefaultNetworkCredentials;
				using (var stream = new MemoryStream())
				{
					using (var streamWriter = new StreamWriter(stream, Encoding.UTF8))
					using (var jsonTextWriter = new JsonTextWriter(streamWriter))
					{
						var commands = new RavenJArray();
						foreach (var doc in batch)
						{
							var metadata = doc.Value<RavenJObject>("@metadata");
							doc.Remove("@metadata");
							commands.Add(new RavenJObject
							             	{
							             		{"Method", "PUT"},
							             		{"Document", doc},
							             		{"Metadata", metadata},
							             		{"Key", metadata.Value<string>("@id")}
							             	});
						}
						commands.WriteTo(jsonTextWriter);
						jsonTextWriter.Flush();
						streamWriter.Flush();
						stream.Flush();
						size = stream.Length;

						using (var netStream = webClient.OpenWrite(instanceUrl + "bulk_docs", "POST"))
						{
							stream.WriteTo(netStream);
							netStream.Flush();
						}
					}
				}

			}
			Console.WriteLine("Wrote {0} documents [{1:#,#} kb] in {2:#,#} ms",
							  batch.Count, Math.Round((double)size / 1024, 2), sw.ElapsedMilliseconds);
			batch.Clear();
		}
	}
}
