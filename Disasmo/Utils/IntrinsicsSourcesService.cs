using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Disasmo.Utils;

public static class IntrinsicsSourcesService
{
    public static async Task<List<IntrinsicsInfo>> ParseIntrinsics(Action<string> progressReporter)
    {
        const string RuntimeUrl = "https://raw.githubusercontent.com/dotnet/runtime/main/src";
        const string IntrinsicsBaseUrl = RuntimeUrl + "/libraries/System.Private.CoreLib/src/System/Runtime/Intrinsics/";

        string[] files = 
        [
            "X86/Aes.cs",
            "X86/Avx.cs",
            "X86/Avx2.cs",
            "X86/Bmi1.cs",
            "X86/Bmi2.cs",
            "X86/Fma.cs",
            "X86/Lzcnt.cs",
            "X86/Pclmulqdq.cs",
            "X86/Popcnt.cs",
            "X86/Sse.cs",
            "X86/Sse2.cs",
            "X86/Sse3.cs",
            "X86/Sse41.cs",
            "X86/Sse42.cs",
            "X86/Ssse3.cs",
            "X86/X86Base.cs",
            "X86/X86Serialize.cs",
            "X86/AvxVnni.cs",

            "X86/Avx512BW.cs",
            "X86/Avx512CD.cs",
            "X86/Avx512DQ.cs",
            "X86/Avx512F.cs",
            "X86/Avx512Vbmi.cs",

            "Arm/AdvSimd.cs",
            "Arm/Aes.cs",
            "Arm/ArmBase.cs",
            "Arm/Crc32.cs",
            "Arm/Dp.cs",
            "Arm/Rdm.cs",
            "Arm/Sha1.cs",
            "Arm/Sha256.cs",

            "Vector64.cs",
            "Vector64_1.cs",
            "Vector128.cs",
            "Vector128_1.cs",
            "Vector256.cs",
            "Vector256_1.cs",
            "Vector512.cs",
            "Vector512_1.cs",
        ];

        var result = new List<IntrinsicsInfo>(8192); 
        foreach (var file in files)
        {
            progressReporter(file);
            var fullUrl = IntrinsicsBaseUrl + file;
            await ParseSourceFile(fullUrl, result);
        }

        return result;
    }

    public static async Task ParseSourceFile(string url, List<IntrinsicsInfo> outputIntrinsics)
    {
        using var client = new HttpClient();
        var content = await client.GetStringAsync(url);
        using var workspace = new AdhocWorkspace();

        var project = 
            workspace
            .AddProject("ParseIntrinsics", LanguageNames.CSharp)
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var document = project.AddDocument("foo", SourceText.From(content));
        var compilation = await document.Project.GetCompilationAsync();
        var root = await document.GetSyntaxRootAsync();
        var model = compilation.GetSemanticModel(root.SyntaxTree);
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();

        foreach (var method in methods)
        {
            var tokens = method.ChildTokens().ToArray();
            if (tokens.Length == 0)
                continue;

            var trivia = tokens.FirstOrDefault().LeadingTrivia;
            var comments = string.Join("\n",
                trivia
                    .ToString().Split('\n').Select(i => i.Trim(' ', '\r', '\t'))
                    .Where(i => !string.IsNullOrWhiteSpace(i)));
            var symbol = model.GetDeclaredSymbol(method);
            var methodName = symbol.ToString()
                .Replace("System.Runtime.Intrinsics.X86.", "")
                .Replace("System.Runtime.Intrinsics.Arm.", "")
                .Replace("System.Runtime.Intrinsics.", "");

            var returnType = method.ReturnType.ToString();
            var intrinsicsInfo = new IntrinsicsInfo(method: returnType + " " + methodName, comments);
            outputIntrinsics.Add(intrinsicsInfo);
        };
    }
}

public class IntrinsicsInfo
{
    public IntrinsicsInfo(string method, string comments)
    {
        _method = method; 
        _comments = comments;

        _cachedDataToCompare = method.ToLower() + '.' + comments.ToLower();
    }

    readonly string _method;
    readonly string _comments;
    readonly string _cachedDataToCompare;

    public string Method { get => _method; set => throw new NotImplementedException(); }
    public string Comments { get => _comments; set => throw new NotImplementedException(); }

    public bool Contains(string content) => _cachedDataToCompare.Contains(content.ToLowerInvariant());

    public override string ToString() => Method;
}