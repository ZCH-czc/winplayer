using System.Net;
using System.Net.Http;

// Only the generated local fixture selected by this test; never contact the URI's host.
internal sealed class GeneratedMediaHandler(string path) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(path)) });
    }
}
