using System.Net;
using System.Net.Http.Headers;

namespace Gmux.Core.Tests;

/// <summary>
/// HttpMessageHandler that returns canned responses for tests. Not thread-safe.
/// </summary>
internal sealed class TestHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public TestHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    public static TestHttpMessageHandler Json(HttpStatusCode status, string body, Action<HttpResponseHeaders>? configureHeaders = null)
        => new((_, _) =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            configureHeaders?.Invoke(response.Headers);
            return Task.FromResult(response);
        });

    public static TestHttpMessageHandler Throws(Exception ex)
        => new((_, _) => Task.FromException<HttpResponseMessage>(ex));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => _handler(request, cancellationToken);
}
