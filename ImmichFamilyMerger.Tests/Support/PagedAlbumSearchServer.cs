using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ImmichFamilyMerger.Tests;

internal sealed class PagedAlbumSearchServer : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/search/metadata", request.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var page = body.RootElement.GetProperty("page").GetInt32();
        RequestCount++;

        object[] items = page switch
        {
            1 =>
            [
                new { id = "asset-1", ownerId = "owner" },
                new { id = "asset-2", ownerId = "owner" },
            ],
            2 => [new { id = "asset-3", ownerId = "owner" }],
            _ => throw new InvalidOperationException($"Unexpected page {page}."),
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                assets = new
                {
                    total = 3,
                    count = items.Length,
                    items,
                    nextPage = page == 1 ? "2" : null,
                },
            }), Encoding.UTF8, "application/json"),
        };
    }
}
