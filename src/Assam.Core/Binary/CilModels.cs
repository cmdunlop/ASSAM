using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Assam.Core.Binary
{
    /// <summary>一個 .NET 方法的 IL 反組譯結果。</summary>
    public sealed class CilMethodListing
    {
        public CilMethodListing()
        {
            Lines = new List<string>();
        }

        /// <summary>所屬型別的完整名稱，例如 "MyApp.Program"。</summary>
        public string TypeName { get; set; }

        public string Name { get; set; }

        /// <summary>可讀的簽章，例如 "static void Main(string[])"。</summary>
        public string Signature { get; set; }

        public uint Rva { get; set; }
        public int MetadataToken { get; set; }
        public int CodeSize { get; set; }
        public int MaxStack { get; set; }
        public int LocalCount { get; set; }
        public bool HasBody { get; set; }

        /// <summary>每一行 IL，例如 "IL_0000: ldstr \"Hello\""。</summary>
        public List<string> Lines { get; private set; }

        public string Text
        {
            get
            {
                var sb = new StringBuilder();
                sb.Append("// ").Append(TypeName).Append("::").Append(Signature ?? Name).Append("\r\n");
                if (!HasBody) return sb.Append("// （沒有 IL 主體：抽象、外部或 P/Invoke 方法）\r\n").ToString();
                sb.Append("// RVA 0x").Append(Rva.ToString("X", CultureInfo.InvariantCulture)).Append("，程式碼 ").Append(CodeSize)
                  .Append(" 位元組，.maxstack ").Append(MaxStack).Append("\r\n");
                foreach (var l in Lines) sb.Append(l).Append("\r\n");
                return sb.ToString();
            }
        }

        public override string ToString()
        {
            return Signature ?? Name;
        }
    }

    public sealed class CilTypeListing
    {
        public CilTypeListing()
        {
            Methods = new List<CilMethodListing>();
        }

        public string Namespace { get; set; }
        public string Name { get; set; }

        public string FullName
        {
            get { return string.IsNullOrEmpty(Namespace) ? Name : Namespace + "." + Name; }
        }

        public List<CilMethodListing> Methods { get; private set; }

        public override string ToString()
        {
            return FullName;
        }
    }

    /// <summary>.NET 組件的 IL 反組譯結果。</summary>
    public sealed class CilDisassemblyResult
    {
        public CilDisassemblyResult()
        {
            Types = new List<CilTypeListing>();
            AssemblyReferences = new List<string>();
            Messages = new List<string>();
        }

        public string AssemblyName { get; set; }
        public string AssemblyVersion { get; set; }

        /// <summary>中繼資料版本字串，例如 "v4.0.30319"。</summary>
        public string MetadataVersion { get; set; }

        /// <summary>進入點方法（例如 "MyApp.Program::Main"），沒有時為 null。</summary>
        public string EntryPoint { get; set; }

        public List<CilTypeListing> Types { get; private set; }
        public List<string> AssemblyReferences { get; private set; }
        public List<string> Messages { get; private set; }
        public int MethodCount { get; set; }
        public bool Truncated { get; set; }
    }
}
