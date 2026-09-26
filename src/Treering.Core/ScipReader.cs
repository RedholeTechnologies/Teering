using Google.Protobuf;
using Scip;

namespace Treering.Core;

/// <summary>
/// index.scip 를 스트리밍으로 읽는다.
///
/// 파일 전체는 <c>Index</c> 메시지 하나이고 <c>documents</c> 는 그 안에 연속으로 놓인
/// length-delimited 레코드다. 그래서 최상위를 태그 단위로 훑으면서 문서를 하나씩 꺼낼 수 있다.
///
/// <c>Index.Parser.ParseFrom()</c> 은 쓰지 않는다 — 파일 전체가 한 번에 힙에 올라온다.
/// 상주 메모리를 「가장 큰 문서 하나」로 묶는 것이 이 클래스의 존재 이유다.
/// </summary>
public static class ScipReader
{
    private const int DocumentsField = 2;

    /// <summary>메타데이터만 읽고 멈춘다. 문서는 건드리지 않는다.</summary>
    public static Metadata? ReadMetadata(string path)
    {
        using var stream = File.OpenRead(path);
        var input = new CodedInputStream(stream);

        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == 1
                && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                var metadata = new Metadata();
                input.ReadMessage(metadata);
                return metadata;
            }

            input.SkipLastField();
        }

        return null;
    }

    /// <summary>
    /// 문서를 하나씩 흘려 보낸다. 호출자가 다음 것을 요청할 때까지 그다음 문서는 읽지 않는다.
    /// 반환된 <see cref="Document"/> 를 붙잡아 두면 스트리밍의 의미가 사라지므로,
    /// 받는 쪽에서 바로 소비하고 버려야 한다.
    /// </summary>
    public static IEnumerable<Document> ReadDocuments(string path)
    {
        using var stream = File.OpenRead(path);
        var input = new CodedInputStream(stream);

        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == DocumentsField
                && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
            {
                var document = new Document();
                input.ReadMessage(document);
                yield return document;
            }
            else
            {
                input.SkipLastField();
            }
        }
    }
}
