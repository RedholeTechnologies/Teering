namespace Treering.Core;

/// <summary>
/// 테스트 코드인가. 이름에서 짐작하지 않고, 언어마다 굳어진 <b>자리</b>로 가린다 —
/// 테스트가 사는 폴더와 파일 이름, 그리고 C# 테스트 프로젝트의 어셈블리 이름.
///
/// C# 은 어셈블리 이름을 함께 본다. 프로젝트 하나만 다시 색인하면 파일 경로가 그 프로젝트
/// 폴더에서 시작해서, 경로만으로는 <c>Shop.Tests/</c> 안의 파일인지 알 수 없다.
/// </summary>
public static class TestCode
{
    /// <summary>테스트가 사는 폴더. <c>spec</c> 은 넣지 않는다 — 명세 문서나 모델을 두는 폴더로도 흔하다.</summary>
    private static readonly string[] Folders = ["test", "tests", "__tests__"];

    /// <summary>C# 테스트 프로젝트가 관례로 쓰는 끝말.</summary>
    private static readonly string[] AssemblyEndings = [".Tests", ".Test", ".UnitTests", ".IntegrationTests"];

    /// <summary>색인 안의 상대 경로가 테스트 파일인가. 구분자는 <c>/</c> 든 <c>\</c> 든 받는다.</summary>
    public static bool IsTestPath(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        foreach (var folder in parts[..^1])
        {
            if (Folders.Contains(folder, StringComparer.OrdinalIgnoreCase) || IsTestAssembly(folder)) return true;
        }

        var file = parts[^1].ToLowerInvariant();

        // Python: pytest 가 찾는 이름.
        if (file == "conftest.py") return true;
        if (file.EndsWith(".py", StringComparison.Ordinal) && (file.StartsWith("test_", StringComparison.Ordinal) || file.EndsWith("_test.py", StringComparison.Ordinal))) return true;

        // TypeScript · JavaScript: order.test.ts, cart.spec.tsx …
        var dot = file.LastIndexOf('.');
        if (dot > 0 && Scripts.Contains(file[(dot + 1)..]))
        {
            var stem = file[..dot];
            if (stem.EndsWith(".test", StringComparison.Ordinal) || stem.EndsWith(".spec", StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static readonly string[] Scripts = ["ts", "tsx", "mts", "cts", "js", "jsx", "mjs", "cjs"];

    /// <summary>C# 어셈블리(SCIP 의 패키지) 이름이 테스트 프로젝트의 것인가.</summary>
    public static bool IsTestAssembly(string name) =>
        AssemblyEndings.Any(ending => name.EndsWith(ending, StringComparison.OrdinalIgnoreCase));
}
