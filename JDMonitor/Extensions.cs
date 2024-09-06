using Newtonsoft.Json;

namespace JDMonitor;

public static class Extensions
{
    public static T FromJson<T>(this string content)
    {
        return JsonConvert.DeserializeObject<T>(content);
    }
    public static string ToJson<T>(this T obj)
    {
        return JsonConvert.SerializeObject(obj,new JsonSerializerSettings(){Formatting = Formatting.Indented});
    }
}