using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Disasmo;

public static class DisassemblyPrettifier
{
    /// <summary>
    /// Handles DOTNET_JitDisasm's asm format:
    /// 
    ///   ; Assembly listing for method Program:MyMethod()
    ///   ; bla-bla
    ///   ; bla-bla
    /// 
    ///   G_M42249_IG01:
    ///          0F1F440000       nop
    ///        
    ///   G_M42249_IG02:
    ///          B82A000000       mov eax, 42
    ///        
    ///   G_M42249_IG03:
    ///          C3               ret
    ///        
    ///   ; Total bytes of code 76, prolog size 5, PerfScore 41.52, instruction count 3, bla-bla for method Program:MyMethod():int (Tier0)
    ///   ; ============================================================
    /// </summary>
    public static string Prettify(string rawAsm, bool minimalComments)
    {
        const string MethodStartedMarker = "; Assembly listing for method ";

        if (!minimalComments)
            return rawAsm;

        try
        {
            var lines = rawAsm.Split(["\r\n", "\n", "\t"], StringSplitOptions.RemoveEmptyEntries);
            var blocks = new List<Block>();

            var prevBlock = BlockType.Unknown;
            var currentMethod = "";

            foreach (var line in lines)
            {
                if (line.StartsWith(MethodStartedMarker))
                {
                    currentMethod = line.Remove(0, MethodStartedMarker.Length);
                }
                else if (currentMethod == "") // In case disasm's output format has changed
                {
                    Log($"Changed disasm's output format was detected.");
                    return rawAsm;
                }

                var currentBlock = BlockType.Unknown;
                if (line.StartsWith(";"))
                {
                    currentBlock = BlockType.Comments;
                }
                else if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                else
                {
                    currentBlock = BlockType.Code;
                    if (Regex.IsMatch(line, @"^\w+:"))
                    {
                        prevBlock = BlockType.Unknown;
                    }
                }

                if (currentBlock != prevBlock)
                {
                    var block = new Block(methodName: currentMethod, type: currentBlock);
                    var data = block.MutableData;
                    data.AppendLine().Append(line).AppendLine();

                    blocks.Add(block);
                    prevBlock = currentBlock;
                }
                else
                {
                    var block = blocks[blocks.Count - 1];
                    var data = block.MutableData;
                    data.Append(line).AppendLine();
                }
            }

            var blocksByMethods = blocks.GroupBy(b => b.MethodName);
            var output = new StringBuilder();

            foreach (var method in blocksByMethods)
            {
                var methodBlocks = (IEnumerable<Block>)method;

                var size = ParseMethodTotalSizes(methodBlocks);

                methodBlocks = methodBlocks.Where(m => m.Type != BlockType.Comments);
                output.Append($"; Method {method.Key}");

                foreach (var block in methodBlocks)
                    output.Append(block.ImmutableData);

                output.Append("; Total bytes of code: ")
                    .Append(size)
                    .AppendLine()
                    .AppendLine();
            }

            return output.ToString();
        }
        catch (Exception ex) when (ex is not MemberAccessException) // In case disasm's output format has changed
        {
            Log($"Exception. Disasm's output format may have changed.");
        }
        catch { }

        return rawAsm;

        static int ParseMethodTotalSizes(IEnumerable<Block> methodBlocks)
        {
            const string Marker = "; Total bytes of code ";

            var lineToParse = methodBlocks.Last().ImmutableData;

            var sizePartStartIndex = lineToParse.IndexOf(Marker) + Marker.Length;
            var commaIndex = lineToParse.IndexOf(',', sizePartStartIndex);

            var sizePartString = commaIndex == -1 ?
                lineToParse.Substring(sizePartStartIndex) :
                lineToParse.Substring(sizePartStartIndex, commaIndex - sizePartStartIndex);

            return int.Parse(sizePartString);
        }   
    }

    private static void Log(string message) => UserLogger.Log($"[{nameof(DisassemblyPrettifier)}] {message}");

    private enum BlockType
    {
        Unknown,
        Comments,
        Code
    }

    private class Block
    {
        public Block(string methodName, BlockType type)
        {
            MethodName = methodName;
            Type = type;

            _mutableData = new StringBuilder(64);
        }

        private StringBuilder _mutableData;
        private string _immutableData;

        public string MethodName { get; private set; }
        public BlockType Type { get; private set; }

        public StringBuilder MutableData
        {
            get
            {
                if (_mutableData is null)
                {
                    var message = "Undefined behavior was detected. An attempt was made to access cleared data.";

                    Log($"Exception. {message}");
                    throw new MemberAccessException(message);
                }

                return _mutableData;
            }
        }

        public string ImmutableData
        {
            get
            {
                if (_immutableData is null)
                {
                    _immutableData = _mutableData.ToString();

                    // Clear the mutable string field after accessing the immutable string field for the first time
                    _mutableData = null;
                }

                return _immutableData;
            } 
        }

        private static void Log(string message) => UserLogger.Log($"[{typeof(DisassemblyPrettifier)}.{typeof(Block)}] {message}");
    }
}