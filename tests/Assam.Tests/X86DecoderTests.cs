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

        // ---------------- AVX／AVX-512／REX2（以 objdump -M intel 驗證過的預期文字）----------------
        // EVEX 資料搬移：vmovdqu8/16/32/64、vmovdqa32/64、vmovups/aps/upd/apd、vmovss/sd、vmovd/q
        [Test]
        public static void EvexMoves()
        {
            Assert.Equal("vmovdqu8 zmm17{k3}{z}, zmm19", Dec(64, 0x62, 0xa1, 0x7f, 0xcb, 0x6f, 0xcb).Text);
            Assert.Equal("vmovdqu16 ymm1, ymm3", Dec(64, 0x62, 0xf1, 0xff, 0x28, 0x6f, 0xcb).Text);
            Assert.Equal("vmovdqu32 zmm17{k2}, zmmword ptr [rcx+rdx*4+0x100]", Dec(64, 0x62, 0xe1, 0x7e, 0x4a, 0x6f, 0x4c, 0x91, 0x04).Text);
            Assert.Equal("vmovdqu64 zmm1{k2}, zmmword ptr [rax+0x40]", Dec(64, 0x62, 0xf1, 0xfe, 0x4a, 0x6f, 0x48, 0x01).Text);
            Assert.Equal("vmovdqa32 zmm17{k3}{z}, zmm19", Dec(64, 0x62, 0xa1, 0x7d, 0xcb, 0x6f, 0xcb).Text);
            Assert.Equal("vmovdqa64 zmm1{k2}, zmmword ptr [rax+0x40]", Dec(64, 0x62, 0xf1, 0xfd, 0x4a, 0x6f, 0x48, 0x01).Text);
            Assert.Equal("vmovups zmm1{k2}, zmmword ptr [rax+0x40]", Dec(64, 0x62, 0xf1, 0x7c, 0x4a, 0x10, 0x48, 0x01).Text);
            Assert.Equal("vmovaps zmmword ptr [rax+0x40]{k2}, zmm1", Dec(64, 0x62, 0xf1, 0x7c, 0x4a, 0x29, 0x48, 0x01).Text);
            Assert.Equal("vmovupd zmm17{k3}{z}, zmm19", Dec(64, 0x62, 0xa1, 0xfd, 0xcb, 0x10, 0xcb).Text);
            Assert.Equal("vmovapd xmm1, xmm3", Dec(64, 0xc5, 0xf9, 0x28, 0xcb).Text);
            Assert.Equal("vmovss xmm1{k2}, dword ptr [rax+0x40]", Dec(64, 0x62, 0xf1, 0x7e, 0x0a, 0x10, 0x48, 0x10).Text);
            Assert.Equal("vmovsd xmm1{k2}, qword ptr [rax+0x40]", Dec(64, 0x62, 0xf1, 0xff, 0x0a, 0x10, 0x48, 0x08).Text);
            Assert.Equal("vmovd xmm1, ebx", Dec(64, 0xc5, 0xf9, 0x6e, 0xcb).Text);
            Assert.Equal("vmovq xmm1, rbx", Dec(64, 0xc4, 0xe1, 0xf9, 0x6e, 0xcb).Text);
        }

        // EVEX 浮點運算：加減乘除 min/max/sqrt、FMA（廣播 {1toN}、嵌入式捨入 {rn-sae}、{sae}）
        [Test]
        public static void EvexFloatArithmetic()
        {
            Assert.Equal("vaddps zmm1{k1}, zmm2, dword ptr [rax+0x40]{1to16}", Dec(64, 0x62, 0xf1, 0x6c, 0x59, 0x58, 0x48, 0x10).Text);
            Assert.Equal("vaddps zmm1{k2}, zmm2, zmm3{rn-sae}", Dec(64, 0x62, 0xf1, 0x6c, 0x1a, 0x58, 0xcb).Text);
            Assert.Equal("vaddpd zmm1{k1}, zmm2, qword ptr [rax+0x40]{1to8}", Dec(64, 0x62, 0xf1, 0xed, 0x59, 0x58, 0x48, 0x08).Text);
            Assert.Equal("vaddss xmm1{k2}, xmm2, xmm3{rz-sae}", Dec(64, 0x62, 0xf1, 0x6e, 0x7a, 0x58, 0xcb).Text);
            Assert.Equal("vsubpd zmm17{k3}{z}, zmm18, zmm19", Dec(64, 0x62, 0xa1, 0xed, 0xc3, 0x5c, 0xcb).Text);
            Assert.Equal("vmulps ymm1, ymm2, ymm3", Dec(64, 0xc5, 0xec, 0x59, 0xcb).Text);
            Assert.Equal("vdivsd xmm1{k2}, xmm2, xmm3{rd-sae}", Dec(64, 0x62, 0xf1, 0xef, 0x3a, 0x5e, 0xcb).Text);
            Assert.Equal("vminps zmm1{k2}, zmm2, zmm3{sae}", Dec(64, 0x62, 0xf1, 0x6c, 0x1a, 0x5d, 0xcb).Text);
            Assert.Equal("vmaxpd zmm1{k2}, zmm2, zmmword ptr [rax+0x40]", Dec(64, 0x62, 0xf1, 0xed, 0x4a, 0x5f, 0x48, 0x01).Text);
            Assert.Equal("vsqrtps zmm1{k2}, zmm3{ru-sae}", Dec(64, 0x62, 0xf1, 0x7c, 0x5a, 0x51, 0xcb).Text);
            Assert.Equal("vsqrtsd xmm1, xmm2, xmm3", Dec(64, 0xc5, 0xeb, 0x51, 0xcb).Text);
            Assert.Equal("vfmadd132ps zmm1{k1}, zmm2, dword ptr [rax+0x40]{1to16}", Dec(64, 0x62, 0xf2, 0x6d, 0x59, 0x98, 0x48, 0x10).Text);
            Assert.Equal("vfmadd213pd zmm1{k2}, zmm2, zmm3{rn-sae}", Dec(64, 0x62, 0xf2, 0xed, 0x1a, 0xa8, 0xcb).Text);
            Assert.Equal("vfmsub231ss xmm1, xmm2, xmm3", Dec(64, 0xc4, 0xe2, 0x69, 0xbb, 0xcb).Text);
            Assert.Equal("vfnmadd132sd xmm1, xmm2, xmm3", Dec(64, 0xc4, 0xe2, 0xe9, 0x9d, 0xcb).Text);
            Assert.Equal("vfmaddsub213ps zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf2, 0x6d, 0x48, 0xa6, 0xcb).Text);
            Assert.Equal("vgetexpps zmm1{k2}, zmm3{sae}", Dec(64, 0x62, 0xf2, 0x7d, 0x1a, 0x42, 0xcb).Text);
            Assert.Equal("vrndscaleps zmm1, zmm3, 0x5", Dec(64, 0x62, 0xf3, 0x7d, 0x48, 0x08, 0xcb, 0x05).Text);
            Assert.Equal("vscalefpd zmm1{k2}, zmm2, zmm3{rn-sae}", Dec(64, 0x62, 0xf2, 0xed, 0x1a, 0x2c, 0xcb).Text);
            Assert.Equal("vrcp14ps zmm1, zmm3", Dec(64, 0x62, 0xf2, 0x7d, 0x48, 0x4c, 0xcb).Text);
        }

        // EVEX 整數運算與邏輯：vpadd/vpsub/vpand/vpor/vpxor（d/q 後綴）、vpmul、vpmax/vpmin
        [Test]
        public static void EvexIntegerLogic()
        {
            Assert.Equal("vpaddb zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf1, 0x6d, 0x48, 0xfc, 0xcb).Text);
            Assert.Equal("vpaddw zmm17{k3}{z}, zmm18, zmm19", Dec(64, 0x62, 0xa1, 0x6d, 0xc3, 0xfd, 0xcb).Text);
            Assert.Equal("vpaddd zmm1{k1}, zmm2, dword ptr [rax+0x40]{1to16}", Dec(64, 0x62, 0xf1, 0x6d, 0x59, 0xfe, 0x48, 0x10).Text);
            Assert.Equal("vpaddq ymm1{k1}, ymm2, qword ptr [rax+0x40]{1to4}", Dec(64, 0x62, 0xf1, 0xed, 0x39, 0xd4, 0x48, 0x08).Text);
            Assert.Equal("vpsubd zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf1, 0x6d, 0x48, 0xfa, 0xcb).Text);
            Assert.Equal("vpsubq zmm1{k1}, zmm2, qword ptr [rax+0x40]{1to8}", Dec(64, 0x62, 0xf1, 0xed, 0x59, 0xfb, 0x48, 0x08).Text);
            Assert.Equal("vpandd zmm1{k1}, zmm2, dword ptr [rax+0x40]{1to16}", Dec(64, 0x62, 0xf1, 0x6d, 0x59, 0xdb, 0x48, 0x10).Text);
            Assert.Equal("vpandq zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf1, 0xed, 0x48, 0xdb, 0xcb).Text);
            Assert.Equal("vpandnd zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf1, 0x6d, 0x48, 0xdf, 0xcb).Text);
            Assert.Equal("vpord zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf1, 0x6d, 0x48, 0xeb, 0xcb).Text);
            Assert.Equal("vporq zmm1{k1}, zmm2, qword ptr [rax+0x40]{1to8}", Dec(64, 0x62, 0xf1, 0xed, 0x59, 0xeb, 0x48, 0x08).Text);
            Assert.Equal("vpxord zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf1, 0x6d, 0x48, 0xef, 0xcb).Text);
            Assert.Equal("vpxorq zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf1, 0xed, 0x48, 0xef, 0xcb).Text);
            Assert.Equal("vpmulld zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf2, 0x6d, 0x48, 0x40, 0xcb).Text);
            Assert.Equal("vpmullq zmm1{k1}, zmm2, qword ptr [rax+0x40]{1to8}", Dec(64, 0x62, 0xf2, 0xed, 0x59, 0x40, 0x48, 0x08).Text);
            Assert.Equal("vpmaxsq zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf2, 0xed, 0x48, 0x3d, 0xcb).Text);
            Assert.Equal("vpminud zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf2, 0x6d, 0x48, 0x3b, 0xcb).Text);
            Assert.Equal("vpabsq zmm1, zmm3", Dec(64, 0x62, 0xf2, 0xfd, 0x48, 0x1f, 0xcb).Text);
            Assert.Equal("vandpd zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf1, 0xed, 0x48, 0x54, 0xcb).Text);
            Assert.Equal("vxorps zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf1, 0x6c, 0x48, 0x57, 0xcb).Text);
            Assert.Equal("vpsadbw zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf1, 0x6d, 0x48, 0xf6, 0xcb).Text);
            Assert.Equal("vpmaddwd zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf1, 0x6d, 0x48, 0xf5, 0xcb).Text);
            Assert.Equal("vpavgb zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf1, 0x6d, 0x48, 0xe0, 0xcb).Text);
        }

        // EVEX 比較與遮罩：vpcmp[u]b/w/d/q（含偽助憶碼）、vpcmpeq/gt、vcmpps、vptestm、vpmovm2*/vpmov*2m、vpblendm、kmov/kand/kor/kxor/kortest/kunpck
        [Test]
        public static void EvexCompareAndMask()
        {
            Assert.Equal("vpcmpeqb k1, zmm2, zmm3", Dec(64, 0x62, 0xf1, 0x6d, 0x48, 0x74, 0xcb).Text);
            Assert.Equal("vpcmpgtd k1{k1}, zmm2, dword ptr [rax+0x40]{1to16}", Dec(64, 0x62, 0xf1, 0x6d, 0x59, 0x66, 0x48, 0x10).Text);
            Assert.Equal("vpcmpnequb k1, zmm2, zmm3", Dec(64, 0x62, 0xf3, 0x6d, 0x48, 0x3e, 0xcb, 0x04).Text);
            Assert.Equal("vpcmpltd k1, zmm2, zmm3", Dec(64, 0x62, 0xf3, 0x6d, 0x48, 0x1f, 0xcb, 0x01).Text);
            Assert.Equal("vpcmpq k1, zmm2, zmm3, 0x3", Dec(64, 0x62, 0xf3, 0xed, 0x48, 0x1f, 0xcb, 0x03).Text);
            Assert.Equal("vpcmpnleuw k1, zmm2, zmm3", Dec(64, 0x62, 0xf3, 0xed, 0x48, 0x3e, 0xcb, 0x06).Text);
            Assert.Equal("kmovw k1, word ptr [rax+0x40]", Dec(64, 0xc5, 0xf8, 0x90, 0x48, 0x40).Text);
            Assert.Equal("kmovq k1, k3", Dec(64, 0xc4, 0xe1, 0xf8, 0x90, 0xcb).Text);
            Assert.Equal("kmovd eax, k3", Dec(64, 0xc5, 0xfb, 0x93, 0xc3).Text);
            Assert.Equal("kandw k1, k2, k3", Dec(64, 0xc5, 0xec, 0x41, 0xcb).Text);
            Assert.Equal("korq k1, k2, k3", Dec(64, 0xc4, 0xe1, 0xec, 0x45, 0xcb).Text);
            Assert.Equal("kxorb k1, k2, k3", Dec(64, 0xc5, 0xed, 0x47, 0xcb).Text);
            Assert.Equal("kortestw k1, k3", Dec(64, 0xc5, 0xf8, 0x98, 0xcb).Text);
            Assert.Equal("kunpckwd k1, k2, k3", Dec(64, 0xc5, 0xec, 0x4b, 0xcb).Text);
            Assert.Equal("kshiftrw k1, k3, 0x5", Dec(64, 0xc4, 0xe3, 0xf9, 0x30, 0xcb, 0x05).Text);
            Assert.Equal("knotd k1, k3", Dec(64, 0xc4, 0xe1, 0xf9, 0x44, 0xcb).Text);
            Assert.Equal("vcmpltps k1, zmm2, zmm3", Dec(64, 0x62, 0xf1, 0x6c, 0x48, 0xc2, 0xcb, 0x01).Text);
            Assert.Equal("vcmpnltss k1, xmm2, xmm3", Dec(64, 0x62, 0xf1, 0x6e, 0x08, 0xc2, 0xcb, 0x05).Text);
            Assert.Equal("vptestmd k1, zmm2, zmm3", Dec(64, 0x62, 0xf2, 0x6d, 0x48, 0x27, 0xcb).Text);
            Assert.Equal("vpmovm2d zmm1, k3", Dec(64, 0x62, 0xf2, 0x7e, 0x48, 0x38, 0xcb).Text);
            Assert.Equal("vpmovb2m k1, zmm3", Dec(64, 0x62, 0xf2, 0x7e, 0x48, 0x29, 0xcb).Text);
            Assert.Equal("vpblendmd zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf2, 0x6d, 0x48, 0x64, 0xcb).Text);
            Assert.Equal("vblendmps zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf2, 0x6d, 0x48, 0x65, 0xcb).Text);
        }

        // EVEX 排列／廣播／擷取／插入：vperm*、vpbroadcast*、vbroadcast*、vpshuf*、vpunpck*、vextracti/f、vinserti/f
        [Test]
        public static void EvexShuffleBroadcast()
        {
            Assert.Equal("vpermd zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf2, 0x6d, 0x48, 0x36, 0xcb).Text);
            Assert.Equal("vpermq zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf2, 0xed, 0x48, 0x36, 0xcb).Text);
            Assert.Equal("vpermb zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf2, 0x6d, 0x48, 0x8d, 0xcb).Text);
            Assert.Equal("vpermi2d zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf2, 0x6d, 0x48, 0x76, 0xcb).Text);
            Assert.Equal("vpbroadcastb zmm1, xmm3", Dec(64, 0x62, 0xf2, 0x7d, 0x48, 0x78, 0xcb).Text);
            Assert.Equal("vpbroadcastq zmm1{k2}, qword ptr [rax+0x40]", Dec(64, 0x62, 0xf2, 0xfd, 0x4a, 0x59, 0x48, 0x08).Text);
            Assert.Equal("vbroadcastss zmm1, xmm3", Dec(64, 0x62, 0xf2, 0x7d, 0x48, 0x18, 0xcb).Text);
            Assert.Equal("vbroadcastsd zmm1{k2}, qword ptr [rax+0x40]", Dec(64, 0x62, 0xf2, 0xfd, 0x4a, 0x19, 0x48, 0x08).Text);
            Assert.Equal("vbroadcastf32x4 zmm1{k2}, xmmword ptr [rax+0x40]", Dec(64, 0x62, 0xf2, 0x7d, 0x4a, 0x1a, 0x48, 0x04).Text);
            Assert.Equal("vbroadcasti64x4 zmm1{k2}, ymmword ptr [rax+0x40]", Dec(64, 0x62, 0xf2, 0xfd, 0x4a, 0x5b, 0x48, 0x02).Text);
            Assert.Equal("vextracti32x4 xmmword ptr [rax+0x40]{k2}, zmm1, 0x5", Dec(64, 0x62, 0xf3, 0x7d, 0x4a, 0x39, 0x48, 0x04, 0x05).Text);
            Assert.Equal("vextracti64x4 ymmword ptr [rax+0x40]{k2}, zmm1, 0x5", Dec(64, 0x62, 0xf3, 0xfd, 0x4a, 0x3b, 0x48, 0x02, 0x05).Text);
            Assert.Equal("vinserti32x4 zmm1, zmm2, xmm3, 0x5", Dec(64, 0x62, 0xf3, 0x6d, 0x48, 0x38, 0xcb, 0x05).Text);
            Assert.Equal("vinserti64x4 zmm1, zmm2, ymm3, 0x5", Dec(64, 0x62, 0xf3, 0xed, 0x48, 0x3a, 0xcb, 0x05).Text);
            Assert.Equal("vextractf32x4 xmm3, zmm1, 0x5", Dec(64, 0x62, 0xf3, 0x7d, 0x48, 0x19, 0xcb, 0x05).Text);
            Assert.Equal("vinsertf64x4 zmm1, zmm2, ymm3, 0x5", Dec(64, 0x62, 0xf3, 0xed, 0x48, 0x1a, 0xcb, 0x05).Text);
            Assert.Equal("vpshufb zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf2, 0x6d, 0x48, 0x00, 0xcb).Text);
            Assert.Equal("vpshufd zmm1{k1}, dword ptr [rax+0x40]{1to16}, 0x5", Dec(64, 0x62, 0xf1, 0x7d, 0x59, 0x70, 0x48, 0x10, 0x05).Text);
            Assert.Equal("vpunpcklbw zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf1, 0x6d, 0x48, 0x60, 0xcb).Text);
            Assert.Equal("vpunpckhqdq zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf1, 0xed, 0x48, 0x6d, 0xcb).Text);
            Assert.Equal("vshuff32x4 zmm1, zmm2, zmm3, 0x5", Dec(64, 0x62, 0xf3, 0x6d, 0x48, 0x23, 0xcb, 0x05).Text);
            Assert.Equal("valignd zmm1, zmm2, zmm3, 0x5", Dec(64, 0x62, 0xf3, 0x6d, 0x48, 0x03, 0xcb, 0x05).Text);
            Assert.Equal("vpalignr zmm1, zmm2, zmm3, 0x5", Dec(64, 0x62, 0xf3, 0x6d, 0x48, 0x0f, 0xcb, 0x05).Text);
            Assert.Equal("vunpcklps zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf1, 0x6c, 0x48, 0x14, 0xcb).Text);
            Assert.Equal("vshufps zmm1, zmm2, zmm3, 0x5", Dec(64, 0x62, 0xf1, 0x6c, 0x48, 0xc6, 0xcb, 0x05).Text);
            Assert.Equal("vpextrb ebx, xmm1, 0x5", Dec(64, 0xc4, 0xe3, 0x79, 0x14, 0xcb, 0x05).Text);
            Assert.Equal("vpinsrd xmm1, xmm2, ebx, 0x5", Dec(64, 0xc4, 0xe3, 0x69, 0x22, 0xcb, 0x05).Text);
            Assert.Equal("vextractps ebx, xmm1, 0x5", Dec(64, 0xc4, 0xe3, 0x79, 0x17, 0xcb, 0x05).Text);
            Assert.Equal("vinsertps xmm1, xmm2, xmm3, 0x5", Dec(64, 0xc4, 0xe3, 0x69, 0x21, 0xcb, 0x05).Text);
            Assert.Equal("vpermilps zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf2, 0x6d, 0x48, 0x0c, 0xcb).Text);
            Assert.Equal("vpermpd zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf2, 0xed, 0x48, 0x16, 0xcb).Text);
        }

        // EVEX 轉換：vcvt*、vpmovzx/sx、vpmovdb/qd 等降轉
        [Test]
        public static void EvexConvert()
        {
            Assert.Equal("vcvtps2pd zmm1{k2}, ymmword ptr [rax+0x40]", Dec(64, 0x62, 0xf1, 0x7c, 0x4a, 0x5a, 0x48, 0x02).Text);
            Assert.Equal("vcvtpd2ps ymm1{k2}, zmm3{rn-sae}", Dec(64, 0x62, 0xf1, 0xfd, 0x1a, 0x5a, 0xcb).Text);
            Assert.Equal("vcvtdq2ps zmm1, zmm3", Dec(64, 0x62, 0xf1, 0x7c, 0x48, 0x5b, 0xcb).Text);
            Assert.Equal("vcvtsi2sd xmm1, xmm2, dword ptr [rax+0x40]", Dec(64, 0xc5, 0xeb, 0x2a, 0x48, 0x40).Text);
            Assert.Equal("vcvtusi2ss xmm1, xmm2, ebx", Dec(64, 0x62, 0xf1, 0x6e, 0x08, 0x7b, 0xcb).Text);
            Assert.Equal("vcvtph2ps zmm1{k2}, ymmword ptr [rax+0x40]", Dec(64, 0x62, 0xf2, 0x7d, 0x4a, 0x13, 0x48, 0x02).Text);
            Assert.Equal("vcvtps2ph ymmword ptr [rax+0x40]{k2}, zmm1, 0x5", Dec(64, 0x62, 0xf3, 0x7d, 0x4a, 0x1d, 0x48, 0x02, 0x05).Text);
            Assert.Equal("vpmovzxbd zmm1{k2}, xmmword ptr [rax+0x40]", Dec(64, 0x62, 0xf2, 0x7d, 0x4a, 0x31, 0x48, 0x04).Text);
            Assert.Equal("vpmovsxwq zmm1, xmm3", Dec(64, 0x62, 0xf2, 0x7d, 0x48, 0x24, 0xcb).Text);
            Assert.Equal("vpmovdb xmm3, zmm1", Dec(64, 0x62, 0xf2, 0x7e, 0x48, 0x31, 0xcb).Text);
            Assert.Equal("vpmovqd ymmword ptr [rax+0x40]{k2}, zmm1", Dec(64, 0x62, 0xf2, 0x7e, 0x4a, 0x35, 0x48, 0x02).Text);
        }

        // EVEX 位移與位元運算：vpslld/vpsrld/vpsrad/vpsraq 立即值、vpternlog、vpopcnt、vpconflict、vpcompress/vpexpand
        [Test]
        public static void EvexShiftBitOps()
        {
            Assert.Equal("vpternlogd zmm1, zmm2, zmm3, 0x5", Dec(64, 0x62, 0xf3, 0x6d, 0x48, 0x25, 0xcb, 0x05).Text);
            Assert.Equal("vpternlogq zmm1{k1}, zmm2, qword ptr [rax+0x40]{1to8}, 0x5", Dec(64, 0x62, 0xf3, 0xed, 0x59, 0x25, 0x48, 0x08, 0x05).Text);
            Assert.Equal("vpslld zmm2, zmm3, 0x5", Dec(64, 0x62, 0xf1, 0x6d, 0x48, 0x72, 0xf3, 0x05).Text);
            Assert.Equal("vpsrld zmm2, zmm3, 0x5", Dec(64, 0x62, 0xf1, 0x6d, 0x48, 0x72, 0xd3, 0x05).Text);
            Assert.Equal("vpsrad zmm2, zmm3, 0x5", Dec(64, 0x62, 0xf1, 0x6d, 0x48, 0x72, 0xe3, 0x05).Text);
            Assert.Equal("vpsraq zmm2, zmm3, 0x5", Dec(64, 0x62, 0xf1, 0xed, 0x48, 0x72, 0xe3, 0x05).Text);
            Assert.Equal("vpsllvd zmm1, zmm2, zmm3", Dec(64, 0x62, 0xf2, 0x6d, 0x48, 0x47, 0xcb).Text);
            Assert.Equal("vpopcntd zmm1, zmm3", Dec(64, 0x62, 0xf2, 0x7d, 0x48, 0x55, 0xcb).Text);
            Assert.Equal("vpopcntb zmm1, zmm3", Dec(64, 0x62, 0xf2, 0x7d, 0x48, 0x54, 0xcb).Text);
            Assert.Equal("vpconflictd zmm1, zmm3", Dec(64, 0x62, 0xf2, 0x7d, 0x48, 0xc4, 0xcb).Text);
            Assert.Equal("vpconflictq zmm1{k1}, qword ptr [rax+0x40]{1to8}", Dec(64, 0x62, 0xf2, 0xfd, 0x59, 0xc4, 0x48, 0x08).Text);
            Assert.Equal("vpcompressd zmmword ptr [rax+0x40]{k2}, zmm1", Dec(64, 0x62, 0xf2, 0x7d, 0x4a, 0x8b, 0x48, 0x10).Text);
            Assert.Equal("vpexpandd zmm1{k2}, zmmword ptr [rax+0x40]", Dec(64, 0x62, 0xf2, 0x7d, 0x4a, 0x89, 0x48, 0x10).Text);
            Assert.Equal("vcompressps zmmword ptr [rax+0x40]{k2}, zmm1", Dec(64, 0x62, 0xf2, 0x7d, 0x4a, 0x8a, 0x48, 0x10).Text);
            Assert.Equal("vexpandpd zmm1{k2}, zmmword ptr [rax+0x40]", Dec(64, 0x62, 0xf2, 0xfd, 0x4a, 0x88, 0x48, 0x08).Text);
        }

        // EVEX 蒐集／散佈（VSIB 索引向量暫存器）
        [Test]
        public static void EvexGatherScatter()
        {
            Assert.Equal("vgatherdps zmm1{k1}, dword ptr [rax+zmm3*4+0x40]", Dec(64, 0x62, 0xf2, 0x7d, 0x49, 0x92, 0x4c, 0x98, 0x10).Text);
            Assert.Equal("vgatherqpd zmm1{k1}, qword ptr [rax+zmm3*4+0x40]", Dec(64, 0x62, 0xf2, 0xfd, 0x49, 0x93, 0x4c, 0x98, 0x08).Text);
            Assert.Equal("vgatherqps ymm1{k1}, dword ptr [rax+zmm3*4+0x40]", Dec(64, 0x62, 0xf2, 0x7d, 0x49, 0x93, 0x4c, 0x98, 0x10).Text);
            Assert.Equal("vpgatherdd zmm1{k1}, dword ptr [rax+zmm3*4+0x40]", Dec(64, 0x62, 0xf2, 0x7d, 0x49, 0x90, 0x4c, 0x98, 0x10).Text);
            Assert.Equal("vscatterdps dword ptr [rax+zmm3*4+0x40]{k1}, zmm1", Dec(64, 0x62, 0xf2, 0x7d, 0x49, 0xa2, 0x4c, 0x98, 0x10).Text);
            Assert.Equal("vscatterqpd qword ptr [rax+zmm3*4+0x40]{k1}, zmm1", Dec(64, 0x62, 0xf2, 0xfd, 0x49, 0xa3, 0x4c, 0x98, 0x08).Text);
            Assert.Equal("vpscatterdq qword ptr [rax+ymm3*4+0x40]{k1}, zmm1", Dec(64, 0x62, 0xf2, 0xfd, 0x49, 0xa0, 0x4c, 0x98, 0x08).Text);
            Assert.Equal("vpscatterqd dword ptr [rax+zmm3*4+0x40]{k1}, ymm1", Dec(64, 0x62, 0xf2, 0x7d, 0x49, 0xa1, 0x4c, 0x98, 0x10).Text);
        }

        // AVX2 補強：vperm2i128/vinserti128/vextracti128/vpermq/vpermpd 立即值、vpermd/vpermps、vgather*（VEX 蒐集）、vpblendd、vpsrlv*、vpmaskmov*
        [Test]
        public static void Avx2Gaps()
        {
            Assert.Equal("vperm2i128 ymm1, ymm2, ymm3, 0x5", Dec(64, 0xc4, 0xe3, 0x6d, 0x46, 0xcb, 0x05).Text);
            Assert.Equal("vperm2f128 ymm1, ymm2, ymm3, 0x5", Dec(64, 0xc4, 0xe3, 0x6d, 0x06, 0xcb, 0x05).Text);
            Assert.Equal("vinserti128 ymm1, ymm2, xmm3, 0x5", Dec(64, 0xc4, 0xe3, 0x6d, 0x38, 0xcb, 0x05).Text);
            Assert.Equal("vinsertf128 ymm1, ymm2, xmmword ptr [rax+0x40], 0x5", Dec(64, 0xc4, 0xe3, 0x6d, 0x18, 0x48, 0x40, 0x05).Text);
            Assert.Equal("vextracti128 xmm3, ymm1, 0x5", Dec(64, 0xc4, 0xe3, 0x7d, 0x39, 0xcb, 0x05).Text);
            Assert.Equal("vextractf128 xmmword ptr [rax+0x40], ymm1, 0x5", Dec(64, 0xc4, 0xe3, 0x7d, 0x19, 0x48, 0x40, 0x05).Text);
            Assert.Equal("vpermq ymm1, ymm3, 0x5", Dec(64, 0xc4, 0xe3, 0xfd, 0x00, 0xcb, 0x05).Text);
            Assert.Equal("vpermpd ymm1, ymm3, 0x5", Dec(64, 0xc4, 0xe3, 0xfd, 0x01, 0xcb, 0x05).Text);
            Assert.Equal("vpermd ymm1, ymm2, ymm3", Dec(64, 0xc4, 0xe2, 0x6d, 0x36, 0xcb).Text);
            Assert.Equal("vpermps ymm1, ymm2, ymm3", Dec(64, 0xc4, 0xe2, 0x6d, 0x16, 0xcb).Text);
            Assert.Equal("vpermilps ymm1, ymm2, ymm3", Dec(64, 0xc4, 0xe2, 0x6d, 0x0c, 0xcb).Text);
            Assert.Equal("vgatherdps xmm1, dword ptr [rax+xmm3*4+0x40], xmm2", Dec(64, 0xc4, 0xe2, 0x69, 0x92, 0x4c, 0x98, 0x40).Text);
            Assert.Equal("vgatherdpd ymm1, qword ptr [rax+xmm3*4+0x40], ymm2", Dec(64, 0xc4, 0xe2, 0xed, 0x92, 0x4c, 0x98, 0x40).Text);
            Assert.Equal("vgatherqps xmm1, dword ptr [rax+ymm3*4+0x40], xmm2", Dec(64, 0xc4, 0xe2, 0x6d, 0x93, 0x4c, 0x98, 0x40).Text);
            Assert.Equal("vgatherqpd ymm1, qword ptr [rax+ymm3*4+0x40], ymm2", Dec(64, 0xc4, 0xe2, 0xed, 0x93, 0x4c, 0x98, 0x40).Text);
            Assert.Equal("vpgatherdd ymm1, dword ptr [rax+ymm3*4+0x40], ymm2", Dec(64, 0xc4, 0xe2, 0x6d, 0x90, 0x4c, 0x98, 0x40).Text);
            Assert.Equal("vpgatherdq ymm1, qword ptr [rax+xmm3*4+0x40], ymm2", Dec(64, 0xc4, 0xe2, 0xed, 0x90, 0x4c, 0x98, 0x40).Text);
            Assert.Equal("vpgatherqd xmm1, dword ptr [rax+ymm3*4+0x40], xmm2", Dec(64, 0xc4, 0xe2, 0x6d, 0x91, 0x4c, 0x98, 0x40).Text);
            Assert.Equal("vpgatherqq ymm1, qword ptr [rax+ymm3*4+0x40], ymm2", Dec(64, 0xc4, 0xe2, 0xed, 0x91, 0x4c, 0x98, 0x40).Text);
            Assert.Equal("vpblendd ymm1, ymm2, ymm3, 0x5", Dec(64, 0xc4, 0xe3, 0x6d, 0x02, 0xcb, 0x05).Text);
            Assert.Equal("vpsrlvd ymm1, ymm2, ymm3", Dec(64, 0xc4, 0xe2, 0x6d, 0x45, 0xcb).Text);
            Assert.Equal("vpsllvq xmm1, xmm2, xmm3", Dec(64, 0xc4, 0xe2, 0xe9, 0x47, 0xcb).Text);
            Assert.Equal("vpsravd ymm1, ymm2, ymm3", Dec(64, 0xc4, 0xe2, 0x6d, 0x46, 0xcb).Text);
            Assert.Equal("vpmaskmovd ymm1, ymm2, ymmword ptr [rax+0x40]", Dec(64, 0xc4, 0xe2, 0x6d, 0x8c, 0x48, 0x40).Text);
            Assert.Equal("vmaskmovps ymm1, ymm2, ymmword ptr [rax+0x40]", Dec(64, 0xc4, 0xe2, 0x6d, 0x2c, 0x48, 0x40).Text);
            Assert.Equal("vtestps ymm1, ymm3", Dec(64, 0xc4, 0xe2, 0x7d, 0x0e, 0xcb).Text);
        }

        // BMI1/BMI2（VEX 編碼的通用暫存器指令）
        [Test]
        public static void VexBmi()
        {
            Assert.Equal("andn eax, edx, ebx", Dec(64, 0xc4, 0xe2, 0x68, 0xf2, 0xc3).Text);
            Assert.Equal("bzhi eax, ebx, edx", Dec(64, 0xc4, 0xe2, 0x68, 0xf5, 0xc3).Text);
            Assert.Equal("mulx eax, edx, ebx", Dec(64, 0xc4, 0xe2, 0x6b, 0xf6, 0xc3).Text);
            Assert.Equal("rorx eax, ebx, 0x5", Dec(64, 0xc4, 0xe3, 0x7b, 0xf0, 0xc3, 0x05).Text);
            Assert.Equal("shlx eax, ebx, edx", Dec(64, 0xc4, 0xe2, 0x69, 0xf7, 0xc3).Text);
            Assert.Equal("sarx eax, ebx, edx", Dec(64, 0xc4, 0xe2, 0x6a, 0xf7, 0xc3).Text);
            Assert.Equal("pext eax, edx, ebx", Dec(64, 0xc4, 0xe2, 0x6a, 0xf5, 0xc3).Text);
        }

        // 既有 VEX 命名的修正：純量三運算元、二運算元（無 vvvv）、窄化轉換、移位
        [Test]
        public static void VexNamingFixes()
        {
            Assert.Equal("vfmadd231ps ymm1, ymm2, ymm3", Dec(64, 0xc4, 0xe2, 0x6d, 0xb8, 0xcb).Text);
            Assert.Equal("vfnmsub132sd xmm1, xmm2, xmm3", Dec(64, 0xc4, 0xe2, 0xe9, 0x9f, 0xcb).Text);
            Assert.Equal("vcvtpd2ps xmm1, ymm3", Dec(64, 0xc5, 0xfd, 0x5a, 0xcb).Text);
            Assert.Equal("vroundps ymm1, ymm3, 0x5", Dec(64, 0xc4, 0xe3, 0x7d, 0x08, 0xcb, 0x05).Text);
            Assert.Equal("vpabsb ymm1, ymm3", Dec(64, 0xc4, 0xe2, 0x7d, 0x1c, 0xcb).Text);
            Assert.Equal("vpsllw ymm2, ymm3, 0x5", Dec(64, 0xc5, 0xed, 0x71, 0xf3, 0x05).Text);
            Assert.Equal("vaddss xmm1, xmm2, xmm3", Dec(64, 0xc5, 0xea, 0x58, 0xcb).Text);
            Assert.Equal("vcmpltps ymm1, ymm2, ymm3", Dec(64, 0xc5, 0xec, 0xc2, 0xcb, 0x01).Text);
            Assert.Equal("vpcmpistri xmm1, xmm3, 0x5", Dec(64, 0xc4, 0xe3, 0x79, 0x63, 0xcb, 0x05).Text);
            Assert.Equal("vcvtph2ps ymm1, xmm3", Dec(64, 0xc4, 0xe2, 0x7d, 0x13, 0xcb).Text);
            Assert.Equal("vcvtps2ph xmm3, ymm1, 0x5", Dec(64, 0xc4, 0xe3, 0x7d, 0x1d, 0xcb, 0x05).Text);
            Assert.Equal("vpextrb ebx, xmm1, 0x5", Dec(64, 0xc4, 0xe3, 0x79, 0x14, 0xcb, 0x05).Text);
            Assert.Equal("vmovd xmm1, ebx", Dec(64, 0xc5, 0xf9, 0x6e, 0xcb).Text);
        }

        // 32 位元模式的 EVEX（0x62 後接 mod=11 的位元組才是 EVEX，否則為 BOUND）
        [Test]
        public static void Evex32()
        {
            Assert.Equal("vaddps zmm1{k1}, zmm2, dword ptr [eax+0x40]{1to16}", Dec(32, 0x62, 0xf1, 0x6c, 0x59, 0x58, 0x48, 0x10).Text);
            Assert.Equal("vpaddd zmm1{k1}, zmm2, dword ptr [eax+0x40]{1to16}", Dec(32, 0x62, 0xf1, 0x6d, 0x59, 0xfe, 0x48, 0x10).Text);
            Assert.Equal("vpternlogd zmm1, zmm2, zmm3, 0x5", Dec(32, 0x62, 0xf3, 0x6d, 0x48, 0x25, 0xcb, 0x05).Text);
            Assert.Equal("vpermd zmm1, zmm2, zmm3", Dec(32, 0x62, 0xf2, 0x6d, 0x48, 0x36, 0xcb).Text);
            Assert.Equal("vcvtps2pd zmm1{k2}, ymmword ptr [eax+0x40]", Dec(32, 0x62, 0xf1, 0x7c, 0x4a, 0x5a, 0x48, 0x02).Text);
            Assert.Equal("kmovw k1, word ptr [eax+0x40]", Dec(32, 0xc5, 0xf8, 0x90, 0x48, 0x40).Text);
            Assert.Equal("vgatherdps zmm1{k1}, dword ptr [eax+zmm3*4+0x40]", Dec(32, 0x62, 0xf2, 0x7d, 0x49, 0x92, 0x4c, 0x98, 0x10).Text);
            Assert.Equal("bound", Dec(32, 0x62, 0x00).Mnemonic);
            Assert.Equal("vmovaps zmm0, zmm1", Dec(32, 0x62, 0xf1, 0x7c, 0x48, 0x28, 0xc1).Text);
            Assert.Equal("vmovaps zmm0, zmmword ptr [eax]", Dec(32, 0x62, 0xd1, 0x7c, 0x48, 0x28, 0x00).Text);
        }


        // EVEX disp8*N 壓縮位移：N 依向量長度／元素大小／廣播而定
        [Test]
        public static void EvexDisp8Scaling()
        {
            Assert.Equal("vmovups zmm1, zmmword ptr [rax+0x40]", Dec(64, 0x62, 0xf1, 0x7c, 0x48, 0x10, 0x48, 0x01).Text);
            Assert.Equal("vmovups ymm1, ymmword ptr [rax+0x20]", Dec(64, 0x62, 0xf1, 0x7c, 0x28, 0x10, 0x48, 0x01).Text);
            Assert.Equal("vmovups xmm1, xmmword ptr [rax+0x10]", Dec(64, 0x62, 0xf1, 0x7c, 0x08, 0x10, 0x48, 0x01).Text);
            Assert.Equal("vmovss xmm1, dword ptr [rax+0x4]", Dec(64, 0x62, 0xf1, 0x7e, 0x08, 0x10, 0x48, 0x01).Text);
            Assert.Equal("vmovsd xmm1, qword ptr [rax+0x8]", Dec(64, 0x62, 0xf1, 0xff, 0x08, 0x10, 0x48, 0x01).Text);
            Assert.Equal("vmovups zmm1, zmmword ptr [rax-0x40]", Dec(64, 0x62, 0xf1, 0x7c, 0x48, 0x10, 0x48, 0xff).Text);
            // 廣播時 N 為元素大小
            Assert.Equal("vaddps zmm1, zmm2, dword ptr [rax+0x4]{1to16}", Dec(64, 0x62, 0xf1, 0x6c, 0x58, 0x58, 0x48, 0x01).Text);
            Assert.Equal("vaddpd zmm1, zmm2, qword ptr [rax+0x8]{1to8}", Dec(64, 0x62, 0xf1, 0xed, 0x58, 0x58, 0x48, 0x01).Text);
            Assert.Equal("vaddpd ymm1, ymm2, qword ptr [rax+0x8]{1to4}", Dec(64, 0x62, 0xf1, 0xed, 0x38, 0x58, 0x48, 0x01).Text);
            // disp32 不縮放；RIP 相對
            Assert.Equal("vmovups zmm1, zmmword ptr [rax+0x100]", Dec(64, 0x62, 0xf1, 0x7c, 0x48, 0x10, 0x88, 0x00, 0x01, 0x00, 0x00).Text);
            var rip = Dec(64, 0x62, 0xf1, 0x7c, 0x48, 0x10, 0x05, 0x00, 0x01, 0x00, 0x00);
            Assert.Equal("vmovups zmm0, zmmword ptr [rip+0x100]", rip.Text);
            Assert.Equal(10, rip.Length);
        }

        // EVEX 暫存器擴充：R'/V'/X 使 xmm/ymm/zmm 可到 31；{k}{z} 遮罩
        [Test]
        public static void EvexExtendedRegisters()
        {
            // P0 = 0x01：R、X、B、R' 皆為 0（反相＝擴充）：reg=0+8+16=24；mod=11 時 rm=1+B(8)+X(16)=25
            Assert.Equal("vmovaps zmm24, zmm25", Dec(64, 0x62, 0x01, 0x7c, 0x48, 0x28, 0xc1).Text);
            Assert.Equal("vmovaps zmm31{k7}{z}, zmm0", Dec(64, 0x62, 0x61, 0x7c, 0xcf, 0x28, 0xf8).Text);
        }

        // EVEX 長度保證：未命名（AVX512-FP16 等）者仍須有效且長度正確
        [Test]
        public static void EvexUnnamedKeepsLength()
        {
            var ph = Dec(64, 0x62, 0xf5, 0x74, 0x48, 0x58, 0xc2); // vaddph zmm0, zmm1, zmm2
            Assert.True(ph.IsValid);
            Assert.Equal(6, ph.Length);
            var phm = Dec(64, 0x62, 0xf5, 0x74, 0x48, 0x58, 0x48, 0x01); // vaddph zmm1, zmm1, [rax+disp8]
            Assert.True(phm.IsValid);
            Assert.Equal(7, phm.Length);
            var cmp = Dec(64, 0x62, 0xf3, 0x74, 0x48, 0xc2, 0xc2, 0x05); // vcmpph k0, zmm1, zmm2, 5（含 imm8）
            Assert.True(cmp.IsValid);
            Assert.Equal(7, cmp.Length);
        }

        // EVEX 無效／截斷：Length=1、IsValid=false，不丟例外
        [Test]
        public static void EvexInvalidAndTruncated()
        {
            byte[] full = { 0x62, 0xf1, 0x6c, 0x58, 0x58, 0x48, 0x01 };
            for (int cut = 0; cut < full.Length; cut++)
            {
                var part = new byte[cut];
                Array.Copy(full, part, cut);
                var ins = new X86Decoder(64).Decode(part, 0, 0);
                Assert.False(ins.IsValid);
                Assert.Equal(1, ins.Length);
                Assert.Equal("(bad)", ins.Mnemonic);
            }
            Assert.True(Dec(64, 0x62, 0xf1, 0x6c, 0x58, 0x58, 0x48, 0x01).IsValid);
            // P1 bit2 必為 1
            Assert.False(Dec(64, 0x62, 0xf1, 0x68, 0x58, 0x58, 0x48, 0x01).IsValid);
            // 廣播位元用在不支援廣播的指令（vpabsb）→ 無效
            Assert.False(Dec(64, 0x62, 0xf2, 0x7d, 0x59, 0x1c, 0x48, 0x02).IsValid);
            // 嵌入式捨入用在不支援的指令（vpaddb）→ 無效
            Assert.False(Dec(64, 0x62, 0xf1, 0x6d, 0x18, 0xfc, 0xcb).IsValid);
        }

        // REX2（APX）：r16–r31
        [Test]
        public static void Rex2Registers()
        {
            Assert.Equal("mov r16, rcx", Dec(64, 0xd5, 0x18, 0x89, 0xc8).Text);
            Assert.Equal("mov eax, r17d", Dec(64, 0xd5, 0x40, 0x89, 0xc8).Text);
            Assert.Equal("push r16", Dec(64, 0xd5, 0x10, 0x50).Text);
            Assert.Equal("pop r17", Dec(64, 0xd5, 0x10, 0x59).Text);
            Assert.Equal("mov r16, qword ptr [r20]", Dec(64, 0xd5, 0x58, 0x8b, 0x04, 0x24).Text);
            Assert.Equal("mov eax, dword ptr [rax+r17*1]", Dec(64, 0xd5, 0x20, 0x8b, 0x04, 0x08).Text);
            Assert.Equal("lea rax, [r16+rcx*1]", Dec(64, 0xd5, 0x18, 0x8d, 0x04, 0x08).Text);
            Assert.Equal("pushp rax", Dec(64, 0xd5, 0x08, 0x50).Text);
            Assert.Equal("popp rax", Dec(64, 0xd5, 0x08, 0x58).Text);
            var jmp = Dec(64, 0xd5, 0x00, 0xa1, 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01);
            Assert.Equal("jmpabs 0x102030405060708", jmp.Text);
            Assert.Equal(11, jmp.Length);
            Assert.Equal(InstructionFlow.Jump, jmp.Flow);
            // 截斷
            Assert.False(Dec(64, 0xd5).IsValid);
            Assert.False(Dec(64, 0xd5, 0x18, 0x89).IsValid);
        }

        // 區段暫存器：8C/8E（含保留的 reg=6/7，objdump 顯示 "?"）
        [Test]
        public static void SegmentMoves()
        {
            Assert.Equal("mov eax, cs", Dec(64, 0x8c, 0xc8).Text);
            Assert.Equal("mov es, ecx", Dec(64, 0x8e, 0xc1).Text);
            Assert.Equal("mov es, cx", Dec(64, 0x66, 0x8e, 0xc1).Text);
            Assert.Equal("mov es, rcx", Dec(64, 0x48, 0x8e, 0xc1).Text);
            Assert.Equal("mov es, word ptr [rcx]", Dec(64, 0x8e, 0x01).Text);
            Assert.Equal("mov eax, ?", Dec(64, 0x8c, 0xf0).Text);
            Assert.Equal("mov ?, eax", Dec(64, 0x8e, 0xf0).Text);
            Assert.Equal("mov word ptr [rax], ?", Dec(64, 0x8c, 0x30).Text);
            Assert.Equal(2, Dec(64, 0x8c, 0xf8).Length);
            Assert.Equal("vmread rcx, rax", Dec(64, 0x0f, 0x78, 0xc1).Text);
            Assert.Equal("vmwrite rax, rcx", Dec(64, 0x0f, 0x79, 0xc1).Text);
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
