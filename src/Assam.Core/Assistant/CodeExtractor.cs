using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Assam.Core.Analysis;
using Assam.Core.Instructions;
using Assam.Core.Text;

namespace Assam.Core.Assistant
{
    /// <summary>把使用者輸入拆成「自然語言問題」與「組合語言程式碼」兩部分。</summary>
    public static class CodeExtractor
    {
        private static readonly HashSet<string> OperandKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "offset", "byte", "word", "dword", "qword", "ptr", "short", "near", "far", "lengthof", "sizeof", "type", "addr",
            "lsl", "lsr", "asr", "ror", "rrx", "low", "high", "sbyte", "sword", "sdword", "tbyte", "real4", "real8", "dup"
        };

        private static readonly HashSet<string> NoOperandInstructions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ret", "nop", "cdq", "cwd", "cbw", "cwde", "cqo", "hlt", "pushad", "popad", "pusha", "popa", "leave", "syscall",
            "clc", "stc", "cmc", "cld", "std", "cli", "sti", "pushfd", "popfd", "movsb", "movsw", "movsd", "stosb", "stosw",
            "stosd", "lodsb", "lodsw", "lodsd", "scasb", "cmpsb", "reti", "ecall", "exit", "iret", "sleep", "wdr"
        };

        public static void Split(string input, out string question, out string code)
        {
            var questionLines = new List<string>();
            var codeLines = new List<string>();
            bool strongCode = false;

            var lines = (input ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            // 若有 ``` 區塊，區塊內一律視為程式碼
            bool inFence = false;
            foreach (var raw in lines)
            {
                string line = raw.TrimEnd();
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    strongCode = true;
                    continue;
                }
                if (inFence) { codeLines.Add(line); continue; }
                if (line.Trim().Length == 0)
                {
                    if (codeLines.Count > 0) codeLines.Add(line);
                    continue;
                }

                int score = CodeScore(line);
                if (score >= 2) { codeLines.Add(line); strongCode = true; continue; }
                if (score == 1) { codeLines.Add(line); continue; }

                // 問句中夾帶單行指令，例如「mov eax, 5 是什麼意思」
                string embedded = ExtractEmbeddedInstruction(line);
                if (embedded != null)
                {
                    codeLines.Add(embedded);
                    strongCode = true;
                }
                questionLines.Add(line);
            }

            int realCode = codeLines.Count(l => l.Trim().Length > 0);
            if (realCode == 0 || (realCode == 1 && !strongCode))
            {
                // 只有一行沒有運算元的指令（例如「loop」），當成問題處理
                question = (input ?? string.Empty).Trim();
                code = string.Empty;
                return;
            }
            question = string.Join(" ", questionLines).Trim();
            code = string.Join("\r\n", codeLines).Trim('\r', '\n');
        }

        /// <summary>0 = 不是程式；1 = 可能是程式（沒有運算元的指令或註解）；2 = 程式；3 = 很確定是程式。</summary>
        public static int CodeScore(string line)
        {
            string comment;
            string codePart = AsmLineParser.StripComment(line, AsmArch.Unknown, out comment).Trim();
            if (codePart.Length == 0) return comment != null && line.TrimStart().StartsWith(";", StringComparison.Ordinal) ? 1 : 0;
            if (TermNormalizer.ContainsCjk(codePart)) return 0;

            var parsed = AsmLineParser.ParseLine(codePart, 0, AsmArch.Unknown);
            if (!parsed.HasMnemonic) return parsed.Label != null ? 2 : 0;
            string mn = parsed.MnemonicLower;
            if (mn.StartsWith(".", StringComparison.Ordinal) && mn.Length > 1 && char.IsLetter(mn[1])) return 3;
            if (AsmLineParser.NamedDirectives.Contains(mn) && parsed.LabelIsName) return 3;
            if (mn == "org" || mn == "end" || mn == "include" || mn == "section" || mn == "global" || mn == "invoke") return 2;

            bool known = InstructionCatalog.Sets.Values.Any(s => s.IsKnown(mn));
            if (!known) return 0;
            if (parsed.Operands.Count == 0) return NoOperandInstructions.Contains(mn) || parsed.Label != null ? 1 : 0;
            if (parsed.Operands.Any(LooksLikeProse)) return 0;
            return parsed.Label != null || parsed.Operands.Count >= 2 ? 3 : 2;
        }

        private static bool LooksLikeProse(string operand)
        {
            var words = Regex.Split(operand.Trim(), @"\s+").Where(w => w.Length > 0).ToList();
            if (words.Count < 2) return false;
            if (OperandKeywords.Contains(words[0])) return false;
            if (operand.IndexOfAny(new[] { '[', '(', '{', '+', '*', '#', '$', '%', '\'', '"', '@' }) >= 0) return false;
            return true;
        }

        private static string ExtractEmbeddedInstruction(string line)
        {
            if (!TermNormalizer.ContainsCjk(line)) return null;
            var sb = new StringBuilder();
            var runs = new List<string>();
            foreach (char c in line)
            {
                if (TermNormalizer.IsCjk(c) || c == '「' || c == '」' || c == '，' || c == '？' || c == '。' || c == '：')
                {
                    if (sb.Length > 0) { runs.Add(sb.ToString()); sb.Clear(); }
                }
                else sb.Append(c);
            }
            if (sb.Length > 0) runs.Add(sb.ToString());

            foreach (var run in runs.OrderByDescending(r => r.Length))
            {
                string candidate = run.Trim().Trim(':', '?', '"', '\'', '`').Trim();
                if (candidate.Length < 4) continue;
                if (CodeScore(candidate) >= 2) return candidate;
            }
            return null;
        }
    }
}
