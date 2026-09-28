using Amazon.Runtime.Internal;
using Skypeek.Core.Logging;

namespace Skypeek.Aws;

/// <summary>Installs the read-only guard and request logger into every AWS SDK client created by this process.</summary>
public sealed class AwsPipeline : IRuntimePipelineCustomizer
{
    private static readonly object Gate = new();
    private static AwsPipeline? _installed;
    private readonly IRequestLogSink _log;

    private AwsPipeline(IRequestLogSink log) => _log = log;

    public string UniqueName => "Skypeek.ReadOnlyGuard";

    public static void Install(IRequestLogSink log)
    {
        lock (Gate)
        {
            if (_installed is not null)
                return;
            _installed = new AwsPipeline(log);
            RuntimePipelineCustomizerRegistry.Instance.Register(_installed);
        }
    }

    public void Customize(Type serviceClientType, RuntimePipeline pipeline)
    {
        // AddHandler inserts at the top, so the guard ends up outermost and runs before logging, signing and HTTP.
        pipeline.AddHandler(new RequestLogHandler(_log));
        pipeline.AddHandler(new ReadOnlyGuardHandler(_log));
    }
}

public static class AwsErrorClassifier
{
    private static readonly HashSet<string> AuthFailureCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ExpiredToken",
        "ExpiredTokenException",
        "InvalidClientTokenId",
        "UnrecognizedClientException",
        "RequestExpired",
        "InvalidSignatureException",
        "SignatureDoesNotMatch",
        "InvalidAccessKeyId",
        "AuthFailure",
    };

    public static bool IsAuthFailure(string? errorCode) => errorCode is not null && AuthFailureCodes.Contains(errorCode);

    public static bool IsAccessDenied(string? errorCode) =>
        errorCode is not null && (errorCode.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) || errorCode.Equals("UnauthorizedOperation", StringComparison.OrdinalIgnoreCase));
}
