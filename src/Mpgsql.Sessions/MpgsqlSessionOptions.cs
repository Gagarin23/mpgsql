namespace Mpgsql;

public enum MpgsqlSslMode
{
    Disable,
    Require,
    VerifyCA,
    VerifyFull
}

/// <summary>Settings for one protocol-3.0 TCP endpoint. No pooling or scheduling policy.</summary>
public sealed record MpgsqlSessionOptions
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5432;
    public required string Username { get; init; }
    public string? Password { get; init; }
    public string? Database { get; init; }
    public string ApplicationName { get; init; } = "Mpgsql.Protocol";
    public MpgsqlSslMode SslMode { get; init; } = MpgsqlSslMode.VerifyFull;
    public string? RootCertificate { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(Username);
        if (Host.IndexOf(',') >= 0 || Host.StartsWith('/') || Host.StartsWith('\\'))
        {
            throw new NotSupportedException("Only one TCP endpoint is supported; multi-host and Unix sockets are unavailable.");
        }
        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port));
        }
        if (!Enum.IsDefined(SslMode))
        {
            throw new ArgumentOutOfRangeException(nameof(SslMode));
        }
        if (ConnectTimeout < TimeSpan.Zero || ConnectTimeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        }
    }
}