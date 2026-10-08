using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Mpgsql;

public sealed class MpgsqlConnectionStringBuilder : DbConnectionStringBuilder
{
    public MpgsqlConnectionStringBuilder() { }
    public MpgsqlConnectionStringBuilder(string connectionString)
    {
        ConnectionString = connectionString;
    }

    [AllowNull]
    public override object this[string keyword]
    {
        get => base[Key(keyword)];
        set => base[Key(keyword)] = value;
    }

    public new string ConnectionString
    {
        get => base.ConnectionString;
        set
        {
            var parsed = new DbConnectionStringBuilder
            {
                ConnectionString = value
            };
            var canonical = new DbConnectionStringBuilder();
            foreach (string key in parsed.Keys)
            {
                canonical[Key(key)] = parsed[key];
            }
            base.ConnectionString = canonical.ConnectionString;
        }
    }

    public string Host
    {
        get => Text("Host", "localhost");
        set => this["Host"] = value;
    }

    public int Port
    {
        get => Number("Port", 5432);
        set => this["Port"] = value;
    }

    public string Username
    {
        get => Text("Username");
        set => this["Username"] = value;
    }

    public string Password
    {
        get => Text("Password");
        set => this["Password"] = value;
    }

    public string Database
    {
        get => Text("Database", Username);
        set => this["Database"] = value;
    }

    public string ApplicationName
    {
        get => Text("Application Name", "Mpgsql.Protocol");
        set => this["Application Name"] = value;
    }

    public MpgsqlSslMode SslMode
    {
        get => Enum.Parse<MpgsqlSslMode>(Text("Ssl Mode", "VerifyFull"), true);
        set => this["Ssl Mode"] = value.ToString();
    }

    public string? RootCertificate
    {
        get => base.TryGetValue("Root Certificate", out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        set
        {
            if (value is null)
            {
                Remove("Root Certificate");
            }
            else
            {
                this["Root Certificate"] = value;
            }
        }
    }

    public int Timeout
    {
        get => Number("Timeout", 15);
        set => this["Timeout"] = value;
    }

    public int CommandTimeout
    {
        get => Number("Command Timeout", 0);
        set => this["Command Timeout"] = value;
    }

    public int MaxPoolSize
    {
        get => Number("Max Pool Size", 10);
        set => this["Max Pool Size"] = value;
    }

    private static string Key(string key)
    {
        return key
                .Trim()
                .ToLowerInvariant() switch
            {
                "host"                                 => "Host",
                "port"                                 => "Port",
                "username" or "user id"                => "Username",
                "password"                             => "Password",
                "database"                             => "Database",
                "application name"                     => "Application Name",
                "ssl mode"                             => "Ssl Mode",
                "root certificate"                     => "Root Certificate",
                "timeout"                              => "Timeout",
                "command timeout"                      => "Command Timeout",
                "max pool size" or "maximum pool size" => "Max Pool Size",
                _                                      => throw new ArgumentException($"Unsupported connection setting '{key}'.", nameof(key))
            };
    }
    public override bool ContainsKey(string keyword)
    {
        return base.ContainsKey(Key(keyword));
    }
    public override bool Remove(string keyword)
    {
        return base.Remove(Key(keyword));
    }
    public override bool TryGetValue(string keyword, [NotNullWhen(true)] out object? value)
    {
        return base.TryGetValue(Key(keyword), out value);
    }
    private string Text(string name, string fallback = "")
    {
        return base.TryGetValue(name, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture)! : fallback;
    }
    private int Number(string name, int fallback)
    {
        return base.TryGetValue(name, out var value) ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : fallback;
    }
    internal MpgsqlSessionOptions ToSessionOptions()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Timeout);
        ArgumentOutOfRangeException.ThrowIfNegative(CommandTimeout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPoolSize);
        var options = new MpgsqlSessionOptions
        {
            Host = Host,
            Port = Port,
            Username = Username,
            Password = base.ContainsKey("Password") ? Password : null,
            Database = Database,
            ApplicationName = ApplicationName,
            SslMode = SslMode,
            RootCertificate = RootCertificate,
            ConnectTimeout = TimeSpan.FromSeconds(Timeout)
        };
        options.Validate();
        return options;
    }
}