using System.Data.Common;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace Mpgsql;

public class MpgsqlException : DbException
{
    public MpgsqlException(string message, Exception? innerException = null) : base(message, innerException) { }
    internal static Exception Map(Exception error)
    {
        return error switch
        {
            MpgsqlServerException server => new MpgsqlPostgresException(server),
            IOException or TimeoutException or SocketException or AuthenticationException or CryptographicException
                => new MpgsqlException(error.Message, error),
            _ => error
        };
    }
}