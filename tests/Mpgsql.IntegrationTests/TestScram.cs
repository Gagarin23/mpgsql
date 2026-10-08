using System.Security.Cryptography;
using System.Text;

namespace Mpgsql.IntegrationTests;

// Test-only SCRAM-SHA-256 for ASCII passwords, without TLS/channel binding or SASLprep.
// RFC 5802 section 3 and RFC 7677 define the proof and server-signature calculations.
internal sealed class TestScram
{
    private readonly string _firstBare;
    private readonly string _nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    private byte[]? _expectedSignature;

    internal TestScram(string user)
    {
        _firstBare = $"n={user.Replace("=", "=3D").Replace(",", "=2C")},r={_nonce}";
    }
    public bool Completed { get; private set; }

    internal byte[] First()
    {
        return Encoding.UTF8.GetBytes("n,," + _firstBare);
    }

    internal byte[] Continue(
        string serverFirst,
        string password
    )
    {
        if (password.Any(c => c > 127))
        {
            throw new NotSupportedException("The live-test helper requires an ASCII password.");
        }
        var fields = Fields(serverFirst);
        if (fields.ContainsKey('m') || !fields['r']
                .StartsWith
                (
                    _nonce,
                    StringComparison.Ordinal
                ) ||
            fields['r'].Length <= _nonce.Length)
        {
            throw new InvalidDataException("Invalid SCRAM server nonce or mandatory extension.");
        }
        var iterations = int.Parse(fields['i']);
        if (iterations is < 1 or > 1_000_000)
        {
            throw new InvalidDataException("Invalid SCRAM iteration count.");
        }
        var salted = Rfc2898DeriveBytes.Pbkdf2
        (
            password,
            Convert.FromBase64String(fields['s']),
            iterations,
            HashAlgorithmName.SHA256,
            32
        );
        var clientKey = HMACSHA256.HashData
        (
            salted,
            "Client Key"u8
        );
        var storedKey = SHA256.HashData(clientKey);
        var finalBare = $"c=biws,r={fields['r']}";
        var authMessage = Encoding.UTF8.GetBytes($"{_firstBare},{serverFirst},{finalBare}");
        var clientSignature = HMACSHA256.HashData
        (
            storedKey,
            authMessage
        );
        for (var i = 0;
             i < clientKey.Length;
             i++)
        {
            clientKey[i] ^= clientSignature[i];
        }
        var serverKey = HMACSHA256.HashData
        (
            salted,
            "Server Key"u8
        );
        _expectedSignature = HMACSHA256.HashData
        (
            serverKey,
            authMessage
        );
        return Encoding.UTF8.GetBytes($"{finalBare},p={Convert.ToBase64String(clientKey)}");
    }

    internal void Verify(string serverFinal)
    {
        var fields = Fields(serverFinal);
        if (_expectedSignature is null || fields.ContainsKey('e') || !fields.TryGetValue
            (
                'v',
                out var signature
            ) ||
            !CryptographicOperations.FixedTimeEquals
            (
                _expectedSignature,
                Convert.FromBase64String(signature)
            ))
        {
            throw new InvalidDataException("SCRAM server signature verification failed.");
        }
        Completed = true;
    }

    private static Dictionary<char, string> Fields(string text)
    {
        return text
            .Split(',')
            .ToDictionary
            (
                field => field.Length >= 3 && field[1] == '=' ? field[0] : throw new InvalidDataException("Invalid SCRAM field."),
                field => field[2..]
            );
    }
}