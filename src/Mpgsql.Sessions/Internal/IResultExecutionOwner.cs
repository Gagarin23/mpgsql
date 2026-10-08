namespace Mpgsql.Internal;

internal interface IResultExecutionOwner
{
    ValueTask EndReaderAsync(bool discard);
}