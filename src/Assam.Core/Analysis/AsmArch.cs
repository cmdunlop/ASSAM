using System;
using System.Collections.Generic;

namespace Assam.Core.Analysis
{
    /// <summary>支援辨識的組合語言架構／語法。</summary>
    public enum AsmArch
    {
        Unknown = 0,
        X86Intel,
        X86Att,
        Arm,
        Arm64,
        Mips,
        RiscV,
        I8051,
        Avr
    }

    public static class AsmArchInfo
    {
        public static readonly AsmArch[] All =
        {
            AsmArch.X86Intel, AsmArch.X86Att, AsmArch.Arm, AsmArch.Arm64,
            AsmArch.Mips, AsmArch.RiscV, AsmArch.I8051, AsmArch.Avr
        };

        public static string DisplayName(AsmArch arch)
        {
            switch (arch)
            {
                case AsmArch.X86Intel: return "x86/x64（Intel 語法：MASM/NASM/TASM）";
                case AsmArch.X86Att: return "x86/x64（AT&T 語法：GAS/GCC）";
                case AsmArch.Arm: return "ARM 32 位元（ARM/Thumb）";
                case AsmArch.Arm64: return "ARM64（AArch64）";
                case AsmArch.Mips: return "MIPS";
                case AsmArch.RiscV: return "RISC-V";
                case AsmArch.I8051: return "8051（MCS-51 單晶片）";
                case AsmArch.Avr: return "AVR（Arduino/ATmega）";
                default: return "未知";
            }
        }

        public static string ShortName(AsmArch arch)
        {
            switch (arch)
            {
                case AsmArch.X86Intel: return "x86";
                case AsmArch.X86Att: return "x86-AT&T";
                case AsmArch.Arm: return "ARM";
                case AsmArch.Arm64: return "ARM64";
                case AsmArch.Mips: return "MIPS";
                case AsmArch.RiscV: return "RISC-V";
                case AsmArch.I8051: return "8051";
                case AsmArch.Avr: return "AVR";
                default: return "未知";
            }
        }

        /// <summary>知識庫條目使用的架構名稱（arch 欄位）。</summary>
        public static string KnowledgeArch(AsmArch arch)
        {
            switch (arch)
            {
                case AsmArch.X86Intel:
                case AsmArch.X86Att: return "x86";
                case AsmArch.Arm: return "ARM";
                case AsmArch.Arm64: return "ARM64";
                case AsmArch.Mips: return "MIPS";
                case AsmArch.RiscV: return "RISC-V";
                case AsmArch.I8051: return "8051";
                case AsmArch.Avr: return "AVR";
                default: return "通用";
            }
        }

        public static string InstructionSetKey(AsmArch arch)
        {
            switch (arch)
            {
                case AsmArch.X86Intel:
                case AsmArch.X86Att: return "x86";
                case AsmArch.Arm: return "arm";
                case AsmArch.Arm64: return "arm64";
                case AsmArch.Mips: return "mips";
                case AsmArch.RiscV: return "riscv";
                case AsmArch.I8051: return "i8051";
                case AsmArch.Avr: return "avr";
                default: return null;
            }
        }

        public static AsmArch FromName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return AsmArch.Unknown;
            AsmArch parsed;
            if (Enum.TryParse(name, true, out parsed)) return parsed;
            foreach (var a in All)
            {
                if (string.Equals(ShortName(a), name, StringComparison.OrdinalIgnoreCase)) return a;
            }
            return AsmArch.Unknown;
        }

        /// <summary>從問句中找出提到的架構（例如「8051 的延遲」→ I8051）。</summary>
        public static List<string> MentionedKnowledgeArchs(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;
            string t = text.ToLowerInvariant();
            if (t.Contains("x86") || t.Contains("8086") || t.Contains("80386") || t.Contains("x64") || t.Contains("masm") ||
                t.Contains("nasm") || t.Contains("irvine") || t.Contains("intel") || t.Contains("dos")) result.Add("x86");
            if (t.Contains("8051") || t.Contains("mcs-51") || t.Contains("mcs51") || t.Contains("單晶片") || t.Contains("89c51") || t.Contains("89s52")) result.Add("8051");
            if (t.Contains("arm64") || t.Contains("aarch64")) result.Add("ARM64");
            else if (t.Contains("arm") || t.Contains("thumb") || t.Contains("cortex")) result.Add("ARM");
            if (t.Contains("mips") || t.Contains("mars") || t.Contains("spim")) result.Add("MIPS");
            if (t.Contains("risc-v") || t.Contains("riscv") || t.Contains("rv32")) result.Add("RISC-V");
            if (t.Contains("avr") || t.Contains("atmega") || t.Contains("arduino")) result.Add("AVR");
            return result;
        }
    }
}
