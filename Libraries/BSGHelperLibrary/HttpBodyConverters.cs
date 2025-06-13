//using ComponentAce.Compression.Libs.zlib;
using ComponentAce.Compression.Libs.zlib;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SIT.BSGHelperLibrary;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Mime;
using System.Text;
using System.Threading.Tasks;
using BSGHelperLibrary;
using Microsoft.Extensions.Primitives;

namespace Paulov.Tarkov.WebServer.DOTNET.Middleware
{
    public static class HttpBodyConverters
    {
        private static readonly JsonSerializerSettings _serializerSettings = new()
        {
            Converters = [new Newtonsoft.Json.Converters.StringEnumConverter()],
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        };
        
        public static bool IsZlibCompressed(byte[] data)
        {
            // We need the first two bytes;
            // First byte: Info (CM/CINFO) Header, should always be 0x78.
            // Second byte: Flags (FLG) Header, should define our compression level.
            if (data.Length < 2 || data[0] != 0x78) return false;

            switch (data[1])
            {
                case 0x01:  // fastest
                case 0x5E:  // low
                case 0x9C:  // normal
                case 0xDA:  // max
                    return true;
            }

            return false;
        }

        public static bool IsZlibCompressed(Stream stream)
        {
            long streamPosition = stream.Position;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(2);
            try
            {
                stream.Seek(0, SeekOrigin.Begin);
                int read = stream.Read(buffer, 0, 2);
                stream.Seek(streamPosition, SeekOrigin.Begin);
                return read == 2 && IsZlibCompressed(buffer); //Gracefully handle no data without needing to clear buffer
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public static bool HeadersAreFromTarkov(StringValues contentEncodings, StringValues userAgents)
        {
            return contentEncodings.Contains("deflate") || userAgents.Any(x => x.StartsWith("Unity"));
        }

        public static bool HeadersAreFromTarkov(IHeaderDictionary header)
        {
            return header.TryGetValue("Content-Encoding", out StringValues contentEncoding) &&
                   header.TryGetValue("user-agent", out StringValues userAgent) &&
                   HeadersAreFromTarkov(contentEncoding, userAgent);
        }

        public static Stream DecompressRequestBodyToStream(HttpRequest request)
        {
            if(!request.Body.CanSeek) request.EnableBuffering();
            request.Body.Seek(0, SeekOrigin.Begin);
            return IsZlibCompressed(request.Body) ? new ZLibStream(request.Body, CompressionMode.Decompress) : request.Body;
        }

        public static async Task<byte[]> DecompressRequestBodyToBytes(HttpRequest request)
        {
            await using Stream decompressedStream = DecompressRequestBodyToStream(request);
            using MemoryStream ms = new();
            await decompressedStream.CopyToAsync(ms);
            return ms.ToArray();
        }

        public static async Task<Dictionary<string, object>> DecompressRequestBodyToDictionary(HttpRequest request)
        {
            await using Stream decompressedStream = DecompressRequestBodyToStream(request);
            using StreamReader sr = new(decompressedStream);
            string body = await sr.ReadToEndAsync();
            
            if (PlootJsonHelper.IsJsonObject(body)) return JsonConvert.DeserializeObject<Dictionary<string, object>>(body);

            return null;
        }

        public static async Task<T> DecompressRequestBodyToType<T>(HttpRequest request)
        {
            string body = Encoding.UTF8.GetString(await DecompressRequestBodyToBytes(request));
            T ret = default;
            if (PlootJsonHelper.IsJsonObject(body)) body.TrySITParseJson(out ret);
            return ret;
        }

        public static async Task CompressDictionaryIntoResponseBody(Dictionary<string, object> dictionary, HttpRequest request, HttpResponse response)
        {
            await CompressStringIntoResponseBody(JsonConvert.SerializeObject(dictionary, _serializerSettings), response);
        }

        public static async Task CompressStringIntoResponseBody(string stringToConvert, HttpResponse response)
        {
            if(response.Headers.IsReadOnly) throw new InvalidOperationException("Response headers are not read-only");
            response.Headers.ContentType = MediaTypeNames.Application.Json;
            response.Headers.ContentEncoding = "deflate";
            response.StatusCode = 200;

            // Zlib compress the data
            if (!string.IsNullOrEmpty(stringToConvert))
            {
                stringToConvert = stringToConvert.Trim();
                await using MemoryStream ms = new(Encoding.UTF8.GetBytes(stringToConvert));
                await using ZLibStream zlibStream = new(response.BodyWriter.AsStream(), CompressionLevel.Optimal);
                await ms.CopyToAsync(zlibStream);
            }
        }

        public static async Task CompressNullIntoResponseBodyBSG(HttpRequest request, HttpResponse response)
        {
            Dictionary<string, object> BSGResponse = new();
            BSGResponse.Add("err", 0);
            BSGResponse.Add("errmsg", null);
            BSGResponse.Add("data", null);
            await CompressDictionaryIntoResponseBody(BSGResponse, request, response);
        }

        public static async Task CompressIntoResponseBodyBSG<T>(T model, HttpResponse response)
        {
            await CompressIntoResponseBodyBSG(await model.SITToJsonAsync(), response);
        }

        public static async Task CompressIntoResponseBodyBSG(string data, HttpResponse response, int errorCode, string errorMessage)
        {
            var resp = "{ 'err': " + errorCode + ", 'errmsg': " + errorMessage + ", 'data': " + data + " }";
            await CompressStringIntoResponseBody(resp, response);
        }


        public static async Task CompressIntoResponseBodyBSG(string data, HttpResponse response)
        {
            data = SanitizeJson(data);
            var resp = "{ 'err': 0, 'errmsg':null, 'data': " + data + " }";
            await CompressStringIntoResponseBody(resp, response);
        }

        public static string SanitizeJson(string json)
        {
            return json.Replace("\r\n", "").Replace("\r", "").Replace("\n", "");
        }

        public static async Task CompressIntoResponseBodyBSG(Dictionary<string, object> dictionary, HttpRequest request, HttpResponse response)
        {
            Dictionary<string, object> BSGResponse = new();
            BSGResponse.Add("err", 0);
            BSGResponse.Add("errmsg", null);
            BSGResponse.Add("data", dictionary);
            await CompressDictionaryIntoResponseBody(BSGResponse, request, response);
        }

        public static void CompressIntoResponseBodyBSG(Dictionary<string, object> dictionary, ref HttpRequest request, ref HttpResponse response)
        {
            Dictionary<string, object> BSGResponse = new();
            BSGResponse.Add("err", 0);
            BSGResponse.Add("errmsg", null);
            BSGResponse.Add("data", dictionary);
            CompressDictionaryIntoResponseBody(BSGResponse, request, response).RunSynchronously();
        }

        public static async Task CompressIntoResponseBodyBSG(JObject obj, HttpRequest request, HttpResponse response)
        {
            Dictionary<string, object> BSGResponse = new();
            BSGResponse.Add("err", 0);
            BSGResponse.Add("errmsg", null);
            BSGResponse.Add("data", obj);
            await CompressDictionaryIntoResponseBody(BSGResponse, request, response);
        }

        public static async Task CompressDictionaryIntoResponseBodyBSG(Dictionary<string, object> dictionary, HttpRequest request, HttpResponse response)
        {
            await CompressIntoResponseBodyBSG(dictionary, request, response);
        }


    }
}
