using System.Collections.Concurrent;
using Amazon;
using Amazon.CloudWatch;
using Amazon.CloudWatchLogs;
using Amazon.EC2;
using Amazon.ECS;
using Amazon.ElastiCache;
using Amazon.ElasticBeanstalk;
using Amazon.ElasticLoadBalancingV2;
using Amazon.RDS;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecurityToken;
using Amazon.SimpleSystemsManagement;
using Skypeek.Core.Credentials;

namespace Skypeek.Aws;

/// <summary>Lazily created SDK clients for one (credentials, region) pair.</summary>
public sealed class AwsClientSet : IDisposable
{
    private readonly AWSCredentials _credentials;
    private readonly RegionEndpoint _region;
    private readonly Lazy<AmazonSecretsManagerClient> _secrets;
    private readonly Lazy<AmazonSimpleSystemsManagementClient> _ssm;
    private readonly Lazy<AmazonElasticBeanstalkClient> _eb;
    private readonly Lazy<AmazonECSClient> _ecs;
    private readonly Lazy<AmazonCloudWatchClient> _cloudWatch;
    private readonly Lazy<AmazonSecurityTokenServiceClient> _sts;
    private readonly Lazy<AmazonCloudWatchLogsClient> _logs;
    private readonly Lazy<AmazonEC2Client> _ec2;
    private readonly Lazy<AmazonRDSClient> _rds;
    private readonly Lazy<AmazonElastiCacheClient> _elastiCache;
    private readonly Lazy<AmazonElasticLoadBalancingV2Client> _elb;

    public AwsClientSet(ProfileCredentials creds, string region)
    {
        Fingerprint = creds.Fingerprint;
        _credentials = creds.Sso is { } sso ? new SsoRoleCredentials(sso)
            : creds.SessionToken is null ? new BasicAWSCredentials(creds.AccessKeyId, creds.SecretAccessKey)
            : new SessionAWSCredentials(creds.AccessKeyId, creds.SecretAccessKey, creds.SessionToken);
        _region = RegionEndpoint.GetBySystemName(region);

        _secrets = new(() => new AmazonSecretsManagerClient(_credentials, Configure(new AmazonSecretsManagerConfig())));
        _ssm = new(() => new AmazonSimpleSystemsManagementClient(_credentials, Configure(new AmazonSimpleSystemsManagementConfig())));
        _eb = new(() => new AmazonElasticBeanstalkClient(_credentials, Configure(new AmazonElasticBeanstalkConfig())));
        _ecs = new(() => new AmazonECSClient(_credentials, Configure(new AmazonECSConfig())));
        _cloudWatch = new(() => new AmazonCloudWatchClient(_credentials, Configure(new AmazonCloudWatchConfig())));
        _sts = new(() => new AmazonSecurityTokenServiceClient(_credentials, Configure(new AmazonSecurityTokenServiceConfig())));
        _logs = new(() => new AmazonCloudWatchLogsClient(_credentials, Configure(new AmazonCloudWatchLogsConfig())));
        _ec2 = new(() => new AmazonEC2Client(_credentials, Configure(new AmazonEC2Config())));
        _rds = new(() => new AmazonRDSClient(_credentials, Configure(new AmazonRDSConfig())));
        _elastiCache = new(() => new AmazonElastiCacheClient(_credentials, Configure(new AmazonElastiCacheConfig())));
        _elb = new(() => new AmazonElasticLoadBalancingV2Client(_credentials, Configure(new AmazonElasticLoadBalancingV2Config())));
    }

    public string Fingerprint { get; }
    public AmazonSecretsManagerClient Secrets => _secrets.Value;
    public AmazonSimpleSystemsManagementClient Ssm => _ssm.Value;
    public AmazonElasticBeanstalkClient ElasticBeanstalk => _eb.Value;
    public AmazonECSClient Ecs => _ecs.Value;
    public AmazonCloudWatchClient CloudWatch => _cloudWatch.Value;
    public AmazonSecurityTokenServiceClient Sts => _sts.Value;
    public AmazonCloudWatchLogsClient Logs => _logs.Value;
    public AmazonEC2Client Ec2 => _ec2.Value;
    public AmazonRDSClient Rds => _rds.Value;
    public AmazonElastiCacheClient ElastiCache => _elastiCache.Value;
    public AmazonElasticLoadBalancingV2Client Elb => _elb.Value;

    private T Configure<T>(T config) where T : ClientConfig
    {
        config.RegionEndpoint = _region;
        config.RetryMode = RequestRetryMode.Standard;
        config.MaxErrorRetry = 3;
        config.Timeout = TimeSpan.FromSeconds(30);
        return config;
    }

    public void Dispose()
    {
        if (_secrets.IsValueCreated) _secrets.Value.Dispose();
        if (_ssm.IsValueCreated) _ssm.Value.Dispose();
        if (_eb.IsValueCreated) _eb.Value.Dispose();
        if (_ecs.IsValueCreated) _ecs.Value.Dispose();
        if (_cloudWatch.IsValueCreated) _cloudWatch.Value.Dispose();
        if (_sts.IsValueCreated) _sts.Value.Dispose();
        if (_logs.IsValueCreated) _logs.Value.Dispose();
        if (_ec2.IsValueCreated) _ec2.Value.Dispose();
        (_credentials as IDisposable)?.Dispose();
        if (_rds.IsValueCreated) _rds.Value.Dispose();
        if (_elastiCache.IsValueCreated) _elastiCache.Value.Dispose();
        if (_elb.IsValueCreated) _elb.Value.Dispose();
    }
}

public sealed class AwsClientFactory : IDisposable
{
    private readonly ConcurrentDictionary<(string Profile, string Region), AwsClientSet> _clients = new();

    /// <summary>Returns cached clients, rebuilding them when the profile's credentials changed.</summary>
    public AwsClientSet Get(ProfileCredentials creds, string region)
    {
        var key = (creds.Name, region);
        while (true)
        {
            if (_clients.TryGetValue(key, out var existing))
            {
                if (existing.Fingerprint == creds.Fingerprint)
                    return existing;
                if (_clients.TryRemove(new KeyValuePair<(string, string), AwsClientSet>(key, existing)))
                    existing.Dispose();
                continue;
            }

            var created = new AwsClientSet(creds, region);
            if (_clients.TryAdd(key, created))
                return created;
            created.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var set in _clients.Values)
            set.Dispose();
        _clients.Clear();
    }
}
