using Microsoft.Extensions.Http;
using Polly;
using Polly.Extensions.Http;

namespace PatchSync.SDK;

public static class HttpHandler
{
  private static PolicyHttpMessageHandler? _policyHandler;

  public static HttpClient CreateHttpClient()
  {
    if (_policyHandler != null)
    {
      var retryPolicy = HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

      var socketHandler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(3) };
      _policyHandler = new PolicyHttpMessageHandler(retryPolicy)
      {
        InnerHandler = socketHandler,
      };
    }

    return new HttpClient(_policyHandler);
  }
}