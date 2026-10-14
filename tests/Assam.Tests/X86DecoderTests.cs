using System;
using Assam.Core.Binary;

namespace Assam.Tests
{
    /// <summary>
    /// X86Decoder 的單元測試（不呼叫 objdump；用手工驗證過的機器碼與測試用 PE 檔）。
    /// </summary>
    public static class X86DecoderTests
    {
        static DecodedInstruction Dec(int bits, ulong addr, params byte[] code)
        {
            return new X86Decoder(bits).Decode(code, 0, addr);
        }

        static DecodedInstruction Dec(int bits, params byte[] code)
        {
            return new X86Decoder(bits).Decode(code, 0, 0x1000);
        }

        // ---------------- 32 位元 ----------------
        [Test]
        public static void Prologue32()
        {
            Assert.Equal("push ebp", Dec(32, 0x55).Text);
            Assert.Equal("mov ebp, esp", Dec(32, 0x89, 0xe5).Text);
            Assert.Equal("sub esp, 0x18", Dec(32, 0x83, 0xec, 0x18).Text);
            Assert.Equal("mov eax, dword ptr [ebp+0x8]", Dec(32, 0x8b, 0x45, 0x08).Text);
        }

        [Test]
        public static void Epilogue32()
        {
            var leave = Dec(32, 0xc9);
            Assert.Equal("leave", leave.Text);
            var ret = Dec(32, 0xc3);
            Assert.Equal("ret", ret.Text);
            Assert.Equal(InstructionFlow.Return, ret.Flow);
        }

        [Test]
        public static void MemoryOperands32()
        {
            // SIB、位移、絕對位址、eiz
            Assert.Equal("mov eax, dword ptr [esp]", Dec(32, 0x8b, 0x04, 0x24).Text);
            Assert.Equal("mov eax, dword ptr [esi+eiz*1]", Dec(32, 0x8b, 0x04, 0x26).Text);
            Assert.Equal("mov eax, dword ptr [esi+eax*1]", Dec(32, 0x8b, 0x04, 0x06).Text);
            Assert.Equal("lea esi, [esi+eiz*1+0x0]", Dec(32, 0x8d, 0xb4, 0x26, 0x00, 0x00, 0x00, 0x00).Text);
            var abs = Dec(32, 0xa1, 0x00, 0x10, 0x40, 0x00); // mov eax, moffs
            Assert.Equal("mov eax, [0x401000]", abs.Text);
            Assert.True(abs.Operands[1].AbsoluteAddress == 0x401000UL);
        }

        [Test]
        public static void RelativeBranches32()
        {
            var call = Dec(32, 0x40100d, 0xe8, 0x75, 0x6c, 0x00, 0x00);
            Assert.Equal("call 0x407c87", call.Text);
            Assert.Equal(InstructionFlow.Call, call.Flow);
            Assert.True(call.BranchTarget == 0x407c87UL, "目標 0x" + (call.BranchTarget ?? 0).ToString("x"));

            var je = Dec(32, 0x40103f, 0x74, 0x57);
            Assert.Equal("je 0x401098", je.Text);
            Assert.Equal(InstructionFlow.ConditionalJump, je.Flow);

            var jmp = Dec(32, 0x401000, 0xeb, 0xfe); // jmp $
            Assert.Equal("jmp 0x401000", jmp.Text);
            Assert.Equal(InstructionFlow.Jump, jmp.Flow);
        }

        [Test]
        public static void ImmediateFormatting32()
        {
            Assert.Equal("and esp, 0xfffffff0", Dec(32, 0x83, 0xe4, 0xf0).Text);
            Assert.Equal("push 0xffffffff", Dec(32, 0x6a, 0xff).Text);
            Assert.Equal("mov eax, 0x401750", Dec(32, 0xb8, 0x50, 0x17, 0x40, 0x00).Text);
            Assert.Equal("add al, 0x5", Dec(32, 0x04, 0x05).Text);
        }

        [Test]
        public static void OpsizePrefix32()
        {
            Assert.Equal("xchg ax, ax", Dec(32, 0x66, 0x90).Text);
            Assert.Equal("mov ax, 0x10b", Dec(32, 0x66, 0xb8, 0x0b, 0x01).Text);
            Assert.Equal("nop", Dec(32, 0x90).Text);
            Assert.Equal("pause", Dec(32, 0xf3, 0x90).Text);
        }

        [Test]
        public static void Groups32()
        {
            Assert.Equal("shl eax, 0x5", Dec(32, 0xc1, 0xe0, 0x05).Text);
            Assert.Equal("shl eax, 1", Dec(32, 0xd1, 0xe0).Text);
            Assert.Equal("sar eax, cl", Dec(32, 0xd3, 0xf8).Text);
            Assert.Equal("test eax, 0x1", Dec(32, 0xf7, 0xc0, 0x01, 0x00, 0x00, 0x00).Text);
            Assert.Equal("neg eax", Dec(32, 0xf7, 0xd8).Text);
            Assert.Equal("inc dword ptr [eax]", Dec(32, 0xff, 0x00).Text);
            Assert.Equal("call eax", Dec(32, 0xff, 0xd0).Text);
            Assert.Equal("jmp dword ptr [eax]", Dec(32, 0xff, 0x20).Text);
            Assert.Equal("push dword ptr [eax]", Dec(32, 0xff, 0x30).Text);
        }

        [Test]
        public static void String32()
        {
            Assert.Equal("movsb", Dec(32, 0xa4).Text);
            Assert.Equal("movsd", Dec(32, 0xa5).Text);
            var rep = Dec(32, 0xf3, 0xa4);
            Assert.Equal("rep", rep.Prefix);
            Assert.Equal("rep movsb", rep.Text);
            var repe = Dec(32, 0xf3, 0xa6);
            Assert.Equal("repe", repe.Prefix);
            var repne = Dec(32, 0xf2, 0xae);
            Assert.Equal("repne", repne.Prefix);
            Assert.Equal("stosd", Dec(32, 0xab).Text);
        }

        // ---------------- 64 位元 ----------------
        [Test]
        public static void Rex64()
        {
            Assert.Equal("mov rax, rbx", Dec(64, 0x48, 0x89, 0xd8).Text);
            Assert.Equal("add r8, r9", Dec(64, 0x4d, 0x01, 0xc8).Text);
            Assert.Equal("mov r15d, 0x1", Dec(64, 0x41, 0xbf, 0x01, 0x00, 0x00, 0x00).Text);
            // spl/bpl/sil/dil 需要 REX
            Assert.Equal("mov sil, al", Dec(64, 0x40, 0x88, 0xc6).Text);
        }

        [Test]
        public static void RipRelative64()
        {
            var ins = Dec(64, 0x1000, 0x48, 0x8b, 0x05, 0x10, 0x00, 0x00, 0x00);
            Assert.Equal("mov rax, qword ptr [rip+0x10]", ins.Text);
            Assert.True(ins.Operands[1].RipRelative);
            Assert.True(ins.Operands[1].AbsoluteAddress == 0x1017UL, "0x" + (ins.Operands[1].AbsoluteAddress ?? 0).ToString("x"));
        }

        [Test]
        public static void Movabs64()
        {
            var ins = Dec(64, 0x48, 0xb8, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11);
            Assert.Equal("movabs rax, 0x1122334455667788", ins.Text);
            Assert.Equal(10, ins.Length);
        }

        [Test]
        public static void Default64StackOps()
        {
            Assert.Equal("push rbp", Dec(64, 0x55).Text);          // 預設 64 位元
            Assert.Equal("push r12", Dec(64, 0x41, 0x54).Text);
            Assert.Equal("pop rbp", Dec(64, 0x5d).Text);
            var jmpReg = Dec(64, 0xff, 0xe0);
            Assert.Equal("jmp rax", jmpReg.Text);                   // 近程間接，預設 64
        }

        [Test]
        public static void InvalidIn64()
        {
            var push_es = Dec(64, 0x06);  // 64 位元無效
            Assert.False(push_es.IsValid);
            Assert.Equal("(bad)", push_es.Mnemonic);
            Assert.Equal(1, push_es.Length);
            Assert.Equal(InstructionFlow.Invalid, push_es.Flow);
        }

        [Test]
        public static void Movsxd64()
        {
            Assert.Equal("movsxd rax, ecx", Dec(64, 0x48, 0x63, 0xc1).Text);
            Assert.Equal("cdqe", Dec(64, 0x48, 0x98).Text);
            Assert.Equal("cqo", Dec(64, 0x48, 0x99).Text);
        }

        // ---------------- 16 位元 ----------------
        [Test]
        public static void Addressing16()
        {
            Assert.Equal("mov ax, word ptr [bx+si]", Dec(16, 0x8b, 0x00).Text);
            Assert.Equal("mov ax, word ptr [bp+di]", Dec(16, 0x8b, 0x03).Text);
            Assert.Equal("mov ax, word ptr [bx]", Dec(16, 0x8b, 0x07).Text);
            Assert.Equal("mov ax, word ptr [bp+0x10]", Dec(16, 0x8b, 0x46, 0x10).Text);
            Assert.Equal("mov ax, word ptr [0x1234]", Dec(16, 0x8b, 0x06, 0x34, 0x12).Text);
        }

        [Test]
        public static void Branch16Wrap()
        {
            // 16 位元近程跳躍的目標會回繞到 16 位元
            var j = Dec(16, 0xfff0, 0xeb, 0x20);
            Assert.True(j.BranchTarget == 0x0012UL, "0x" + (j.BranchTarget ?? 0).ToString("x"));
        }

        [Test]
        public static void Jcxz16()
        {
            Assert.Equal("jcxz", Dec(16, 0xe3, 0x05).Mnemonic);
            Assert.Equal("jecxz", Dec(32, 0xe3, 0x05).Mnemonic);
            Assert.Equal("jrcxz", Dec(64, 0xe3, 0x05).Mnemonic);
        }

        // ---------------- SSE / VEX / x87 ----------------
        [Test]
        public static void Sse()
        {
            Assert.Equal("movaps xmm0, xmmword ptr [rdi]", Dec(64, 0x0f, 0x28, 0x07).Text);
            Assert.Equal("movss xmm0, dword ptr [rsp+0x10]", Dec(64, 0xf3, 0x0f, 0x10, 0x44, 0x24, 0x10).Text);
            Assert.Equal("pxor xmm0, xmm1", Dec(64, 0x66, 0x0f, 0xef, 0xc1).Text);
            Assert.Equal("movd xmm0, eax", Dec(64, 0x66, 0x0f, 0x6e, 0xc0).Text);
            Assert.Equal("movq xmm0, rax", Dec(64, 0x66, 0x48, 0x0f, 0x6e, 0xc0).Text);
            Assert.Equal("pshufd xmm0, xmm1, 0x1b", Dec(64, 0x66, 0x0f, 0x70, 0xc1, 0x1b).Text);
            Assert.Equal("cmpnlesd xmm3, xmm0", Dec(64, 0xf2, 0x0f, 0xc2, 0xd8, 0x06).Text);
        }

        [Test]
        public static void Vex()
        {
            Assert.Equal("vmovdqu ymm0, ymmword ptr [rdi]", Dec(64, 0xc5, 0xfe, 0x6f, 0x07).Text);
            Assert.Equal("vzeroupper", Dec(64, 0xc5, 0xf8, 0x77).Text);
            Assert.Equal("vpxor xmm0, xmm1, xmm2", Dec(64, 0xc5, 0xf1, 0xef, 0xc2).Text);
            Assert.Equal("vaddps ymm0, ymm1, ymm2", Dec(64, 0xc5, 0xf4, 0x58, 0xc2).Text);
        }

        [Test]
        public static void Vex2IsLesIn32()
        {
            // 32 位元下 C4/C5 在 mod!=3 時為 LES/LDS
            var les = Dec(32, 0xc4, 0x00); // les eax, fword? mod0 -> memory
            Assert.Equal("les", les.Mnemonic);
        }

        [Test]
        public static void X87()
        {
            Assert.Equal("fld dword ptr [rbp-0x8]", Dec(64, 0xd9, 0x45, 0xf8).Text);
            Assert.Equal("fld qword ptr [rbp-0x8]", Dec(64, 0xdd, 0x45, 0xf8).Text);
            Assert.Equal("fadd st, st(1)", Dec(64, 0xd8, 0xc1).Text);
            Assert.Equal("faddp st(1), st", Dec(64, 0xde, 0xc1).Text);
            Assert.Equal("fstp st(1)", Dec(64, 0xdd, 0xd9).Text);
            Assert.Equal("fxch st(1)", Dec(64, 0xd9, 0xc9).Text);
            Assert.Equal("fsqrt", Dec(64, 0xd9, 0xfa).Text);
            Assert.Equal("fild dword ptr [rax]", Dec(64, 0xdb, 0x00).Text);
            // fwait 合併
            Assert.Equal("fstsw ax", Dec(64, 0x9b, 0xdf, 0xe0).Text);
        }

        [Test]
        public static void Flow()
        {
            Assert.Equal(InstructionFlow.Stop, Dec(32, 0xf4).Flow);           // hlt
            Assert.Equal(InstructionFlow.Stop, Dec(32, 0x0f, 0x0b).Flow);     // ud2
            Assert.Equal(InstructionFlow.Stop, Dec(32, 0xcc).Flow);           // int3
            Assert.Equal(InstructionFlow.Return, Dec(32, 0xc2, 0x04, 0x00).Flow); // ret 0x4
            Assert.Equal(InstructionFlow.Return, Dec(32, 0xcf).Flow);         // iret
        }

        // ---------------- 無效 / 截斷 ----------------
        [Test]
        public static void Truncated()
        {
            // 需要 ModRM 但緩衝區結束
            var t1 = Dec(32, 0x8b);
            Assert.False(t1.IsValid);
            Assert.Equal(1, t1.Length);
            Assert.Equal("(bad)", t1.Mnemonic);
            // 需要位移但不足
            var t2 = Dec(32, 0x8b, 0x05, 0x00);
            Assert.False(t2.IsValid);
            Assert.Equal(1, t2.Length);
        }

        [Test]
        public static void EmptyAndBounds()
        {
            var dec = new X86Decoder(32);
            var e = dec.Decode(new byte[0], 0, 0);
            Assert.False(e.IsValid);
            Assert.Equal(1, e.Length);
            // offset 超出範圍不丟例外
            var o = dec.Decode(new byte[] { 0x90 }, 5, 0);
            Assert.False(o.IsValid);
        }

        [Test]
        public static void MaxLength()
        {
            // 連續 15 個以上的前綴 -> 無效（超過 15 位元組）
            var many = new byte[20];
            for (int i = 0; i < 20; i++) many[i] = 0x66;
            var ins = new X86Decoder(32).Decode(many, 0, 0);
            Assert.True(ins.Length <= 15);
        }

        // ---------------- 永不丟例外（1 MB 隨機模糊測試）----------------
        [Test]
        public static void NeverThrowsFuzz()
        {
            var rnd = new Random(0xA55A);
            var buf = new byte[1024 * 1024];
            rnd.NextBytes(buf);
            foreach (int bits in new[] { 16, 32, 64 })
            {
                var dec = new X86Decoder(bits);
                int off = 0;
                long n = 0;
                while (off < buf.Length)
                {
                    var ins = dec.Decode(buf, off, (ulong)off);
                    Assert.True(ins.Length >= 1 && ins.Length <= 15, "長度超範圍: " + ins.Length);
                    Assert.True(ins.Bytes.Length == ins.Length, "Bytes 長度不符");
                    off += ins.Length;
                    n++;
                }
                Assert.True(n > 0);
            }
        }

        [Test]
        public static void NeverReadsPastBuffer()
        {
            // 對每個尾端位置都解碼，確認不會讀出界、不丟例外
            var rnd = new Random(7);
            var buf = new byte[4096];
            rnd.NextBytes(buf);
            var dec = new X86Decoder(64);
            for (int start = Math.Max(0, buf.Length - 20); start < buf.Length; start++)
            {
                var ins = dec.Decode(buf, start, (ulong)start);
                Assert.True(start + ins.Length <= buf.Length, "讀出緩衝區範圍");
            }
        }

        // ---------------- 端到端：測試用 PE 的 .text ----------------
        [Test]
        public static void DecodeHello32Text()
        {
            DecodeSectionEndToEnd("hello32.exe", 32);
        }

        [Test]
        public static void DecodeHello64Text()
        {
            DecodeSectionEndToEnd("hello64.exe", 64);
        }

        static void DecodeSectionEndToEnd(string file, int bits)
        {
            var b = BinaryLoader.Load(BinaryLoaderTests.TestFile(file));
            Assert.Equal(bits, b.Bits);
            PeSection text = null;
            foreach (var s in b.Sections)
                if (s.Name == ".text") { text = s; break; }
            Assert.NotNull(text);
            byte[] code = b.ReadVa(text.VirtualAddress, (int)text.RawSize);
            Assert.True(code.Length > 64, "讀取 .text 失敗");
            var dec = new X86Decoder(bits);
            int off = 0, total = 0, valid = 0;
            while (off < code.Length)
            {
                var ins = dec.Decode(code, off, text.VirtualAddress + (ulong)off);
                total++;
                if (ins.IsValid) valid++;
                off += ins.Length;
            }
            Assert.True(total > 50, "指令太少: " + total);
            // 線性掃描真實 .text 應幾乎全部有效（結尾可能有對齊填補）
            double ratio = valid / (double)total;
            Assert.True(ratio > 0.95, file + " 有效率過低: " + ratio.ToString("0.000"));
        }
    }
}
