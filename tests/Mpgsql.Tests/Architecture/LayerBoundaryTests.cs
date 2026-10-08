using Mpgsql.Converters;
using Mpgsql.Copy;
using Mpgsql.Protocol;
using Mpgsql.Types;

namespace Mpgsql.Tests.Architecture;

public sealed class LayerBoundaryTests
{
    [Fact]
    public void LowerLayerIsIndependentOfClientAndTransport()
    {
        var assembly = typeof(FrontendMessageWriter).Assembly;
        Assert.Equal("Mpgsql", assembly.GetName().Name);
        Assert.All(new[]
        {
            typeof(BackendMessageReader), typeof(Int64Converter), typeof(PgNumeric),
            typeof(TypeOid), typeof(BinaryCopyOperation)
        }, type => Assert.Same(assembly, type.Assembly));

        var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name).ToArray();
        Assert.DoesNotContain("Mpgsql.Client", references);
        Assert.DoesNotContain("Mpgsql.Sessions", references);
        Assert.DoesNotContain("Mpgsql.Multiplexing", references);
        Assert.DoesNotContain("System.IO.Pipelines", references);
        Assert.All(assembly.GetExportedTypes(), type => Assert.True(
            type == typeof(TypeOid) || type.Namespace is
                "Mpgsql.Protocol" or "Mpgsql.Converters" or "Mpgsql.Copy" or "Mpgsql.Types",
            $"Unexpected lower-layer API: {type.FullName}"));
    }

    [Fact]
    public void ClientDependsOnLowerLayerInASeparateAssembly()
    {
        var assembly = typeof(MpgsqlDataSource).Assembly;
        Assert.Equal("Mpgsql.Client", assembly.GetName().Name);
        Assert.NotSame(typeof(FrontendMessageWriter).Assembly, assembly);
        Assert.All(new[]
        {
            typeof(MpgsqlConnection), typeof(MpgsqlCommand), typeof(MpgsqlBatch), typeof(MpgsqlDataReader),
            typeof(MpgsqlTransaction), typeof(MpgsqlParameter), typeof(MpgsqlFactory)
        }, type => Assert.Same(assembly, type.Assembly));
        Assert.Contains("Mpgsql", assembly.GetReferencedAssemblies().Select(reference => reference.Name));
        Assert.Contains("Mpgsql.Sessions", assembly.GetReferencedAssemblies().Select(reference => reference.Name));
        Assert.DoesNotContain("Mpgsql.Multiplexing", assembly.GetReferencedAssemblies().Select(reference => reference.Name));
        var sessions = typeof(MpgsqlMessageSession).Assembly;
        Assert.Equal("Mpgsql.Sessions", sessions.GetName().Name);
        Assert.All(new[] {typeof(MpgsqlQueryBatch), typeof(MpgsqlPreparedStatement), typeof(MpgsqlResultReader), typeof(MpgsqlParameterValue)}, type => Assert.Same(sessions, type.Assembly));
        Assert.DoesNotContain("Mpgsql.Client", sessions.GetReferencedAssemblies().Select(reference => reference.Name));
        Assert.DoesNotContain("Mpgsql.Multiplexing", sessions.GetReferencedAssemblies().Select(reference => reference.Name));
        var multiplexing = typeof(MpgsqlMultiplexingDataSource).Assembly;
        Assert.Equal("Mpgsql.Multiplexing", multiplexing.GetName().Name);
        Assert.DoesNotContain("Mpgsql.Client", multiplexing.GetReferencedAssemblies().Select(reference => reference.Name));
        Assert.Contains("Mpgsql.Sessions", multiplexing.GetReferencedAssemblies().Select(reference => reference.Name));
    }
}