using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Assam.Core.Instructions;

namespace Assam.Core.Knowledge
{
    /// <summary>
    /// 知識庫：內建知識（內嵌 .md 資源）+ 自動由指令表產生的指令卡 + 使用者資料夾中的 .md + 使用者教學。
    /// 全部資料都在本機。
    /// </summary>
    public sealed class KnowledgeBase
    {
        private readonly List<KnowledgeEntry> _entries = new List<KnowledgeEntry>();
        private readonly Dictionary<string, KnowledgeEntry> _byId =
            new Dictionary<string, KnowledgeEntry>(StringComparer.OrdinalIgnoreCase);

        public KnowledgeBase()
        {
            LoadErrors = new List<string>();
        }

        /// <summary>每次內容變動就加 1，搜尋引擎據此判斷是否需要重建索引。</summary>
        public int Version { get; private set; }

        public List<string> LoadErrors { get; private set; }

        public IReadOnlyList<KnowledgeEntry> Entries
        {
            get { return _entries; }
        }

        public int Count
        {
            get { return _entries.Count; }
        }

        public KnowledgeEntry Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            KnowledgeEntry e;
            return _byId.TryGetValue(id, out e) ? e : null;
        }

        /// <summary>載入內建知識與指令卡。</summary>
        public void LoadBuiltIn()
        {
            var asm = typeof(KnowledgeBase).Assembly;
            foreach (var name in asm.GetManifestResourceNames().OrderBy(n => n, StringComparer.Ordinal))
            {
                if (name.IndexOf(".Knowledge.Data.", StringComparison.Ordinal) < 0 ||
                    !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    continue;
                string text = InstructionCatalog.ReadResource(asm, name);
                string source = "內建:" + name.Substring(name.IndexOf(".Knowledge.Data.", StringComparison.Ordinal) + 16);
                foreach (var e in KnowledgeParser.Parse(text, source))
                    AddOrUpdate(e);
            }
            LoadInstructionEntries();
        }

        /// <summary>每一個指令都產生一張「指令卡」，讓問答也能查到單一指令。</summary>
        public void LoadInstructionEntries()
        {
            foreach (var set in InstructionCatalog.Sets.Values)
            {
                string arch = KnowledgeArchOfSet(set.Key);
                foreach (var info in set.All)
                {
                    var tags = new List<string>(info.Mnemonics.Select(m => m.ToUpperInvariant()));
                    tags.Add(info.Category);
                    tags.Add(arch);
                    var body = new StringBuilder();
                    body.Append("語法：").Append(info.Syntax).Append('\n');
                    body.Append("分類：").Append(info.Category).Append('\n');
                    body.Append("影響旗標：").Append(info.Flags).Append('\n');
                    body.Append('\n').Append(info.Description).Append('\n');
                    if (!string.IsNullOrEmpty(info.ActionTemplate))
                        body.Append("\n動作（虛擬碼）：").Append(info.ActionTemplate.Replace("||", "；或")).Append('\n');

                    AddOrUpdate(new KnowledgeEntry
                    {
                        Id = "ins-" + set.Key + "-" + info.PrimaryMnemonic.Replace('.', '_'),
                        Title = info.MnemonicText.ToUpperInvariant() + " 指令（" + set.DisplayName + "）",
                        Category = KnowledgeEntry.CategoryInstruction,
                        Arch = arch,
                        Tags = tags,
                        Body = body.ToString().TrimEnd(),
                        Source = "內建指令表:" + set.Key
                    });
                }
            }
        }

        public static string KnowledgeArchOfSet(string setKey)
        {
            switch ((setKey ?? string.Empty).ToLowerInvariant())
            {
                case "x86": return "x86";
                case "arm": return "ARM";
                case "arm64": return "ARM64";
                case "mips": return "MIPS";
                case "riscv": return "RISC-V";
                case "i8051": return "8051";
                case "avr": return "AVR";
                default: return "通用";
            }
        }

        /// <summary>載入資料夾中的所有 .md 知識檔，回傳成功載入的條目數。</summary>
        public int LoadDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return 0;
            int count = 0;
            foreach (var file in Directory.GetFiles(directory, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string text = File.ReadAllText(file, Encoding.UTF8);
                    foreach (var e in KnowledgeParser.Parse(text, "檔案:" + Path.GetFileName(file)))
                    {
                        e.IsUserEntry = true;
                        AddOrUpdate(e);
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    LoadErrors.Add(Path.GetFileName(file) + "：" + ex.Message);
                }
            }
            return count;
        }

        public void AddOrUpdate(KnowledgeEntry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            if (string.IsNullOrEmpty(entry.Id)) entry.Id = "entry-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (entry.Tags == null) entry.Tags = new List<string>();

            KnowledgeEntry existing;
            if (_byId.TryGetValue(entry.Id, out existing))
            {
                int idx = _entries.IndexOf(existing);
                _entries[idx] = entry;
            }
            else
            {
                _entries.Add(entry);
            }
            _byId[entry.Id] = entry;
            Version++;
        }

        public bool Remove(string id)
        {
            KnowledgeEntry e;
            if (string.IsNullOrEmpty(id) || !_byId.TryGetValue(id, out e)) return false;
            _byId.Remove(id);
            _entries.Remove(e);
            Version++;
            return true;
        }

        public IEnumerable<string> Architectures()
        {
            return _entries.Select(e => e.Arch ?? "通用").Distinct(StringComparer.OrdinalIgnoreCase)
                           .OrderBy(a => a == "通用" ? "0" : a, StringComparer.OrdinalIgnoreCase);
        }
    }
}
