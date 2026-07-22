using System.Net;

namespace ImmichFamilyMerger;

internal sealed class ImmichApiException : Exception
{
    public ImmichApiException(HttpStatusCode statusCode, string message) : base(message) => StatusCode = statusCode;

    public HttpStatusCode StatusCode { get; }
}
