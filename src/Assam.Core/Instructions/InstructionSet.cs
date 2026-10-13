using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Assam.Core.Instructions
{
    /// <summary>單一架構的指令表（從內嵌的 Instructions/Data/*.txt 載入）。</summary>
    public sealed class InstructionSet
    {
        private static readonly string[] ArmConditions =
        {
            "eq", "ne", "cs", "hs", "cc", "lo", "mi", "pl", "vs", "vc", "hi", "ls", "ge", "lt", "gt", "le", "al"
        };

        private readonly Dictionary<string, InstructionInfo> _map =
            new Dictionary<string, InstructionInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly List<InstructionInfo> _all = new List<InstructionInfo>();

        public InstructionSet(string key, string displayName)
        {
            Key = key;
            DisplayName = displayName;
        }

        public string Key { get; private set; }
        public string DisplayName { get; set; }

        public IList<InstructionInfo> All
        {
            get { return _all; }
        }

        public IEnumerable<string> Mnemonics
        {
            get { return _map.Keys; }
        }

        public void Add(InstructionInfo info)
        {
            _all.Add(info);
            foreach (var m in info.Mnemonics)
                _map[m] = info;
        }

        /// <summary>查詢指令；會處理 AT&amp;T 字尾（movl）、ARM 條件碼字尾（addeq、bne）等變化。</summary>
        public InstructionInfo Find(string mnemonic)
        {
            if (string.IsNullOrWhiteSpace(mnemonic)) return null;
            string m = mnemonic.Trim().ToLowerInvariant();
            InstructionInfo info;
            if (_map.TryGetValue(m, out info)) return info;

            switch (Key)
            {
                case "x86":
                    return FindX86Variant(m);
                case "arm":
                    return FindArmVariant(m);
                case "arm64":
                    if (m.StartsWith("b.", StringComparison.Ordinal) && _map.TryGetValue("b.cond", out info)) return info;
                    return FindArmVariant(m);
                default:
                    return null;
            }
        }

        public bool IsKnown(string mnemonic)
        {
            return Find(mnemonic) != null;
        }

        /// <summary>只做完全比對（不處理字尾變化）。</summary>
        public bool IsExact(string mnemonic)
        {
            return !string.IsNullOrEmpty(mnemonic) && _map.ContainsKey(mnemonic.Trim());
        }

        private InstructionInfo FindX86Variant(string m)
        {
            InstructionInfo info;
            switch (m)
            {
                case "cltd": return Lookup("cdq");
                case "cltq": return Lookup("cdqe");
                case "cwtl": return Lookup("cwde");
                case "cbtw": return Lookup("cbw");
                case "cqto": return Lookup("cqo");
                case "cwtd": return Lookup("cwd");
            }
            if (m.Length == 6 && m.StartsWith("movz", StringComparison.Ordinal)) return Lookup("movzx");
            if (m.Length == 6 && m.StartsWith("movs", StringComparison.Ordinal)) return Lookup("movsx");
            if (m.Length >= 3 && "bwlq".IndexOf(m[m.Length - 1]) >= 0 &&
                _map.TryGetValue(m.Substring(0, m.Length - 1), out info))
                return info;
            return null;
        }

        private InstructionInfo FindArmVariant(string m)
        {
            InstructionInfo info;
            if (m.EndsWith(".w", StringComparison.Ordinal) || m.EndsWith(".n", StringComparison.Ordinal))
            {
                m = m.Substring(0, m.Length - 2);
                if (_map.TryGetValue(m, out info)) return info;
            }

            foreach (var cond in ArmConditions)
            {
                if (m.Length > cond.Length && m.EndsWith(cond, StringComparison.Ordinal))
                {
                    string baseName = m.Substring(0, m.Length - cond.Length);
                    if (_map.TryGetValue(baseName, out info)) return info;
                    if (baseName.EndsWith("s", StringComparison.Ordinal) && baseName.Length > 1 &&
                        _map.TryGetValue(baseName.Substring(0, baseName.Length - 1), out info)) return info;
                }
            }
            if (m.Length > 2 && m.EndsWith("s", StringComparison.Ordinal))
            {
                string noS = m.Substring(0, m.Length - 1);
                if (_map.TryGetValue(noS, out info)) return info;
                foreach (var cond in ArmConditions)
                {
                    if (noS.Length > cond.Length && noS.EndsWith(cond, StringComparison.Ordinal) &&
                        _map.TryGetValue(noS.Substring(0, noS.Length - cond.Length), out info)) return info;
                }
            }
            return null;
        }

        private InstructionInfo Lookup(string m)
        {
            InstructionInfo info;
            return _map.TryGetValue(m, out info) ? info : null;
        }

        /// <summary>
        /// 解析指令資料檔。每行：助憶碼(以 / 分隔) | 分類 | 影響旗標 | 語法 | 說明 | 動作樣板(可省略)。
        /// 以 # 開頭為註解，@arch / @name 設定架構代碼與顯示名稱。
        /// </summary>
        public static InstructionSet Parse(string text, string defaultKey)
        {
            var set = new InstructionSet(defaultKey, defaultKey);
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.StartsWith("@arch ", StringComparison.Ordinal)) { set.Key = line.Substring(6).Trim(); continue; }
                if (line.StartsWith("@name ", StringComparison.Ordinal)) { set.DisplayName = line.Substring(6).Trim(); continue; }

                var parts = line.Split('|');
                if (parts.Length < 5) continue;
                var info = new InstructionInfo
                {
                    ArchKey = set.Key,
                    Mnemonics = parts[0].Split('/').Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToArray(),
                    Category = parts[1].Trim(),
                    Flags = parts[2].Trim(),
                    Syntax = parts[3].Trim(),
                    Description = parts[4].Trim(),
                    ActionTemplate = parts.Length > 5 ? string.Join("|", parts, 5, parts.Length - 5).Trim() : null
                };
                if (info.Mnemonics.Length > 0) set.Add(info);
            }
            return set;
        }
    }

    /// <summary>所有架構的指令表（啟動時從內嵌資源載入一次）。</summary>
    public static class InstructionCatalog
    {
        private static readonly object Sync = new object();
        private static Dictionary<string, InstructionSet> _sets;

        public static IDictionary<string, InstructionSet> Sets
        {
            get
            {
                EnsureLoaded();
                return _sets;
            }
        }

        public static InstructionSet Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            EnsureLoaded();
            InstructionSet set;
            return _sets.TryGetValue(key, out set) ? set : null;
        }

        private static void EnsureLoaded()
        {
            if (_sets != null) return;
            lock (Sync)
            {
                if (_sets != null) return;
                var sets = new Dictionary<string, InstructionSet>(StringComparer.OrdinalIgnoreCase);
                var asm = typeof(InstructionCatalog).Assembly;
                foreach (var name in asm.GetManifestResourceNames())
                {
                    if (name.IndexOf(".Instructions.Data.", StringComparison.Ordinal) < 0 ||
                        !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string text = ReadResource(asm, name);
                    string fileKey = name.Substring(0, name.Length - 4);
                    fileKey = fileKey.Substring(fileKey.LastIndexOf('.') + 1);
                    var set = InstructionSet.Parse(text, fileKey);
                    sets[set.Key] = set;
                }
                _sets = sets;
            }
        }

        internal static string ReadResource(Assembly asm, string name)
        {
            using (var stream = asm.GetManifestResourceStream(name))
            {
                if (stream == null) return string.Empty;
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    return reader.ReadToEnd();
            }
        }
    }
}
