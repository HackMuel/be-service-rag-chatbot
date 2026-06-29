using System.Net;

namespace be_service.Services;

public class UpstreamServiceException : Exception
{
    public string ServiceName { get; }
    public HttpStatusCode StatusCode { get; }

    public UpstreamServiceException(string serviceName, HttpStatusCode statusCode)
        : base($"{serviceName} call failed with status {(int)statusCode} ({statusCode}).")
    {
        ServiceName = serviceName;
        StatusCode = statusCode;
    }
}
