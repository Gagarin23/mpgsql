using System.Buffers;
using Mpgsql.Protocol;

namespace Mpgsql.Tests.Protocol;

// Only test fixtures erase the concrete type; product writers use generic, statically selected encoders.
public abstract class FrontendTestCase
{
    public abstract int GetByteCount();
    public abstract int Write(Span<byte> destination);
    public abstract void Write(IBufferWriter<byte> destination);
    public static implicit operator FrontendTestCase(RawFrontendMessage message) => new TypedCase<RawFrontendMessage>(message);
    public static implicit operator FrontendTestCase(TextMessage message) => new TypedCase<TextMessage>(message);
    public static implicit operator FrontendTestCase(EmptyMessage message) => new TypedCase<EmptyMessage>(message);
    public static implicit operator FrontendTestCase(TargetMessage message) => new TypedCase<TargetMessage>(message);
    public static implicit operator FrontendTestCase(ExecuteMessage message) => new TypedCase<ExecuteMessage>(message);
    public static implicit operator FrontendTestCase(ParseMessage message) => new TypedCase<ParseMessage>(message);
    public static implicit operator FrontendTestCase(BindMessage message) => new TypedCase<BindMessage>(message);
    public static implicit operator FrontendTestCase(FunctionCallMessage message) => new TypedCase<FunctionCallMessage>(message);
    public static implicit operator FrontendTestCase(StartupMessage message) => new TypedCase<StartupMessage>(message);
    public static implicit operator FrontendTestCase(EncryptionRequestMessage message) => new TypedCase<EncryptionRequestMessage>(message);
    public static implicit operator FrontendTestCase(CancelRequestMessage message) => new TypedCase<CancelRequestMessage>(message);
    public static implicit operator FrontendTestCase(SaslInitialResponseMessage message) => new TypedCase<SaslInitialResponseMessage>(message);

    private sealed class TypedCase<T>(T message) : FrontendTestCase where T : struct, IFrontendMessage<T>
    {
        private readonly T _message = message;
        public override int GetByteCount() => FrontendMessageWriter.GetByteCount(in _message);
        public override int Write(Span<byte> destination) => FrontendMessageWriter.Write(in _message,
            destination);
        public override void Write(IBufferWriter<byte> destination) => FrontendMessageWriter.Write(in _message,
            destination);
    }
}