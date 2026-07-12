using System.Net.Http;

namespace Minecraft;

internal static class PortableHttpClient
{
    public static HttpClient Shared { get; } = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MinecraftPortable/1.0");
        return client;
    }
}
