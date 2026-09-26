using Google.Protobuf;
using Scip;
using Treering.Core;

namespace Treering.Core.Tests;

/// <summary>
/// The index is read field by field, not parsed whole, so its layout is ours to get right. Metadata
/// names the indexer and the project root; misread, the loader picks the wrong rules for a language
/// or cannot place deleted files. A field it does not know must be stepped over, not read as a file.
/// </summary>
public sealed class ScipReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tr-{Guid.NewGuid():N}.scip");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static Document Doc(string path) => new() { RelativePath = path, Language = "C#" };

    private static readonly Metadata Shop = new() { ProjectRoot = "file:///shop", ToolInfo = new ToolInfo { Name = "scip-dotnet", Version = "1" } };

    /// <summary>Writes top-level fields in exactly the given order, as another writer might.</summary>
    private void Write(params (int Field, IMessage? Message, ulong? Varint)[] fields)
    {
        using var file = File.Create(_path);
        var output = new CodedOutputStream(file);
        foreach (var (field, message, varint) in fields)
        {
            if (message is not null)
            {
                output.WriteTag(field, WireFormat.WireType.LengthDelimited);
                output.WriteMessage(message);
            }
            else
            {
                output.WriteTag(field, WireFormat.WireType.Varint);
                output.WriteUInt64(varint!.Value);
            }
        }

        output.Flush();
    }

    [Fact]
    public void Metadata_written_after_the_documents_is_still_found()
    {
        Write((2, Doc("Cart.cs"), null), (2, Doc("Order.cs"), null), (1, Shop, null));

        Assert.Equal("file:///shop", ScipReader.ReadMetadata(_path)!.ProjectRoot);
    }

    [Fact]
    public void An_index_without_metadata_says_so()
    {
        Write((2, Doc("Cart.cs"), null));

        Assert.Null(ScipReader.ReadMetadata(_path));
    }

    [Fact]
    public void A_field_the_reader_does_not_know_is_stepped_over()
    {
        Write((1, Shop, null), (2, Doc("Cart.cs"), null), (99, null, 7UL), (2, Doc("Order.cs"), null));

        Assert.Equal(["Cart.cs", "Order.cs"], ScipReader.ReadDocuments(_path).Select(document => document.RelativePath));
        Assert.Equal("scip-dotnet", ScipReader.ReadMetadata(_path)!.ToolInfo.Name);
    }
}
