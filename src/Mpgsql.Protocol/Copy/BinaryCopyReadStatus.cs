namespace Mpgsql.Copy;

public enum BinaryCopyReadStatus
{
    NeedMoreData,
    Row,
    Completed
}