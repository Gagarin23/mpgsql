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
            typeof(MpgsqlMessageSession), typeof(MpgsqlQueryBatch), typeof(MpgsqlResultReader),
            typeof(MpgsqlPreparedStatement), typeof(MpgsqlParameter), typeof(MpgsqlConnection),
            typeof(MpgsqlCommand), typeof(MpgsqlBatch)
        }, type => Assert.Same(assembly, type.Assembly));
        Assert.Contains("Mpgsql", assembly.GetReferencedAssemblies().Select(reference => reference.Name));
    }
}
