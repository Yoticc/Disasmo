using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Disasmo.Utils;

public static class IntrinsicsSourcesService
{
    public static async Task<List<IntrinsicsInfo>> ParseIntrinsics(Action<string> progressReporter)
    {
        progressReporter("Fetching data from Github...");

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

        var contents = await DownloadContents(progressReporter, files);

        var progressReport = $"Compiling source code...";
        progressReporter(progressReport);

        var result = await ParseSourceFile(contents);       

        return result;
    }

    private static async Task<string[]> DownloadContents(Action<string> progressReporter, string[] files)
    {
        using var client = new HttpClient();
        var contents = new string[files.Length];
        var tasks = new Task[files.Length];

        var taskIndex = 0;
        var counter = new ProgressCounter();
        foreach (var file in files)
        {
            tasks[taskIndex] = DownloadContent(
                progressReporter, 
                client,
                file,
                contents, 
                taskIndex++, 
                counter,
                files.Length);
        }

        await Task.WhenAll(tasks);

        return contents;

        static async Task DownloadContent(
            Action<string> progressReporter,
            HttpClient client,
            string file,
            string[] contents, 
            int contentIndex,
            ProgressCounter counter,
            int totalFiles)
        {
            const string RuntimeUrl = "https://raw.githubusercontent.com/dotnet/runtime/main/src/";
            const string IntrinsicsBaseUrl = RuntimeUrl + "libraries/System.Private.CoreLib/src/System/Runtime/Intrinsics/";

            var fileUrl = IntrinsicsBaseUrl + file;
            contents[contentIndex] = await client.GetStringAsync(fileUrl);

            var progressReport = $"Fetching data from Github...\nFetched {++counter.Count} of {totalFiles} files.";
            progressReporter(progressReport);
        }
    }

    private static async Task<List<IntrinsicsInfo>> ParseSourceFile(string[] contents)
    {
        var intrinsicsInfos = new List<IntrinsicsInfo>(8192);
        using var workspace = new AdhocWorkspace();

        var project =
            workspace
            .AddProject("ParseIntrinsicsProject", LanguageNames.CSharp)
            .WithMetadataReferences([MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        foreach (var content in contents)
        {
            var document = project.AddDocument(name: Guid.NewGuid().ToString(), content);
            project = document.Project;
        }

        var compilation = await project.GetCompilationAsync();
        foreach (var document in project.Documents)
        {
            var documentSyntaxRoot = await document.GetSyntaxRootAsync();
            var model = compilation.GetSemanticModel(documentSyntaxRoot.SyntaxTree);
            var methods = documentSyntaxRoot.DescendantNodes().OfType<MethodDeclarationSyntax>();

            foreach (var method in methods)
            {
                var tokens = method.ChildTokens();
                if (!tokens.Any())
                    continue;

                var rootToken = tokens.FirstOrDefault();

                var triviaToken = rootToken.LeadingTrivia;
                var commentsParts =
                    triviaToken.ToString()
                    .Split('\n')
                    .Select(i => i.Trim(' ', '\r', '\t'))
                    .Where(i => !string.IsNullOrWhiteSpace(i));

                var comments = string.Join("\n", commentsParts);
                var symbol = model.GetDeclaredSymbol(method);
                var methodName = 
                    symbol.ToString()
                    .Replace("System.Runtime.Intrinsics.X86.", "")
                    .Replace("System.Runtime.Intrinsics.Arm.", "")
                    .Replace("System.Runtime.Intrinsics.", "");

                var returnType = method.ReturnType.ToString();

                var intrinsicsInfo = new IntrinsicsInfo(method: returnType + " " + methodName, comments);
                intrinsicsInfos.Add(intrinsicsInfo);
            }
        }

        return intrinsicsInfos;
    }

    private class ProgressCounter
    {
        private int count;

        public int Count 
        {
            get => count; 
            set => Interlocked.Exchange(ref count, value);
        }
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

    private readonly string _method;
    private readonly string _comments;
    private readonly string _cachedDataToCompare;

    public string Method { get => _method; set => throw new NotImplementedException(); }
    public string Comments { get => _comments; set => throw new NotImplementedException(); }

    public bool Contains(string content) => _cachedDataToCompare.Contains(content.ToLowerInvariant());

    public override string ToString() => Method;
}