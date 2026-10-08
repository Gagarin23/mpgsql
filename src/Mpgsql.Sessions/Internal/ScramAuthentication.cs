using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mpgsql.Internal;

// SCRAM-SHA-256 (RFC 5802/7677). PostgreSQL ignores the SASL username; startup owns it.
internal sealed class ScramAuthentication
{
    private readonly string _first;
    private readonly string _nonce;
    private bool _challenged;
    private byte[]? _signature;

    internal ScramAuthentication(string? nonce = null)
    {
        _nonce = nonce ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        _first = "n=,r=" + _nonce;
    }
    internal bool Completed { get; private set; }

    internal byte[] First()
    {
        return Encoding.UTF8.GetBytes("n,," + _first);
    }

    internal byte[] Continue(string serverFirst, string password)
    {
        if (_challenged || Completed)
        {
            throw new InvalidDataException("Repeated SCRAM challenge.");
        }
        _challenged = true;
        var fields = Fields(serverFirst);
        if (fields.ContainsKey('m') || !fields.TryGetValue('r', out var nonce)
                                    || !nonce.StartsWith(_nonce, StringComparison.Ordinal) || nonce.Length <= _nonce.Length
                                    || !fields.TryGetValue('s', out var salt) || !fields.TryGetValue('i', out var count)
                                    || !int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
                                    || iterations is < 1 or > 1_000_000)
        {
            throw new InvalidDataException("Invalid SCRAM nonce, salt or iteration count.");
        }
        var secret = Encoding.UTF8.GetBytes(SaslPrep.Normalize(password));
        var salted = Rfc2898DeriveBytes.Pbkdf2(secret, Convert.FromBase64String(salt), iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            var clientKey = HMACSHA256.HashData(salted, "Client Key"u8);
            var storedKey = SHA256.HashData(clientKey);
            var final = "c=biws,r=" + nonce;
            var transcript = Encoding.UTF8.GetBytes(_first + "," + serverFirst + "," + final);
            var proof = HMACSHA256.HashData(storedKey, transcript);
            for (var i = 0;
                 i < proof.Length;
                 i++)
            {
                proof[i] ^= clientKey[i];
            }
            var serverKey = HMACSHA256.HashData(salted, "Server Key"u8);
            _signature = HMACSHA256.HashData(serverKey, transcript);
            CryptographicOperations.ZeroMemory(clientKey);
            CryptographicOperations.ZeroMemory(serverKey);
            return Encoding.UTF8.GetBytes(final + ",p=" + Convert.ToBase64String(proof));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(salted);
        }
    }

    internal void Verify(string serverFinal)
    {
        var fields = Fields(serverFinal);
        if (_signature is null || Completed || fields.ContainsKey('e') || !fields.TryGetValue('v', out var encoded))
        {
            throw new InvalidDataException("Invalid SCRAM server final message.");
        }
        var signature = Convert.FromBase64String(encoded);
        var valid = CryptographicOperations.FixedTimeEquals(_signature, signature);
        CryptographicOperations.ZeroMemory(_signature);
        _signature = null;
        if (!valid)
        {
            throw new InvalidDataException("SCRAM server signature verification failed.");
        }
        Completed = true;
    }

    private static Dictionary<char, string> Fields(string text)
    {
        var result = new Dictionary<char, string>();
        foreach (var item in text.Split(','))
        {
            if (item.Length < 3 || item[1] != '=' || !result.TryAdd(item[0], item[2..]))
            {
                throw new InvalidDataException("Invalid or duplicate SCRAM attribute.");
            }
        }
        return result;
    }
}