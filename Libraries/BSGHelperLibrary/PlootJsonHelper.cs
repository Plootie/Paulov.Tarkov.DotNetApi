using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace BSGHelperLibrary;

public static class PlootJsonHelper
{
    public static IEnumerable<Dictionary<string, object>> ParseStreamAsEnumerable(Stream stream)
    {
        return JsonSerializer.DeserializeAsyncEnumerable<Dictionary<string, object>>(stream).ToBlockingEnumerable();
    }

    public static bool IsJsonObject(string data)
    {
        data = data.Trim();
        return (data.StartsWith('{') && data.EndsWith('}')) || (data.StartsWith('[') && data.EndsWith(']'));
    }
}