using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Assam.Core.Binary;

namespace Assam.Tests
{
    public static class CilDisassemblerTests
    {
        private static CilDisassemblyResult Disassemble(string testFile, int maxMethods = 5000)
        {
            return CilDisassembler.Disassemble(BinaryLoader.Load(BinaryLoaderTests.TestFile(testFile)), maxMethods);
        }

        private static CilMethodListing FindMethod(CilDisassemblyResult r, string typeFullName, string methodName)
        {
            var type = r.Types.FirstOrDefault(t => t.FullName == typeFullName);
            Assert.NotNull(type, "找不到型別 " + typeFullName + "：" + string.Join(", ", r.Types.Select(t => t.FullName)));
            var method = type.Methods.FirstOrDefault(m => m.Name == methodName);
            Assert.NotNull(method, "找不到方法 " + typeFullName + "::" + methodName);
            return method;
        }

        /// <summary>只取 IL 指令行（略過 "//" 註解行）。</summary>
        private static string[] Code(CilMethodListing m)
        {
            return m.Lines.Where(l => l.StartsWith("IL_", StringComparison.Ordinal)).ToArray();
        }

        /// <summary>內部錯誤訊息（表示有程式碼路徑丟出例外，只是被攔截）。</summary>
        private static void AssertNoInternalErrors(CilDisassemblyResult r, string context)
        {
            foreach (var m in r.Messages)
            {
                Assert.DoesNotContain("未預期的錯誤", m, context);
                Assert.DoesNotContain("個方法無法完整解碼", m, context);
            }
        }

        [Test]
        public static void HelloCsAssemblyInfo()
        {
            var r = Disassemble("hello_cs.exe");
            Assert.Equal(0, r.Messages.Count, string.Join(" | ", r.Messages));
            Assert.Equal("hello_cs", r.AssemblyName);
            Assert.Equal("0.0.0.0", r.AssemblyVersion);
            Assert.Equal("v4.0.30319", r.MetadataVersion);
            Assert.Equal("AssamSample.Program::Main", r.EntryPoint);
            Assert.True(r.AssemblyReferences.Contains("mscorlib 4.0.0.0"), string.Join(", ", r.AssemblyReferences));
            Assert.Equal(2, r.MethodCount);
            Assert.False(r.Truncated);
            var type = r.Types.Single();
            Assert.Equal("AssamSample", type.Namespace);
            Assert.Equal("Program", type.Name);
            Assert.Equal("AssamSample.Program", type.FullName);
        }

        [Test]
        public static void HelloCsMainBody()
        {
            var r = Disassemble("hello_cs.exe");
            var main = FindMethod(r, "AssamSample.Program", "Main");
            Assert.Equal("static int32 Main(string[])", main.Signature);
            Assert.True(main.HasBody);
            Assert.Equal(0x06000002, main.MetadataToken);
            Assert.Equal(0x2058u, main.Rva);
            Assert.Equal(67, main.CodeSize);
            Assert.Equal(2, main.MaxStack);
            Assert.Equal(2, main.LocalCount);
            Assert.True(main.Lines.Contains("// .locals init (int32 V_0, int32 V_1)"), string.Join("\n", main.Lines));

            var code = Code(main);
            Assert.Equal("IL_0000: ldc.i4.0", code[0]);
            Assert.True(code.Contains("IL_0004: br IL_0016"), "分支目標");
            Assert.True(code.Contains("IL_000b: call int32 AssamSample.Program::Square(int32)"), "呼叫 Square");
            Assert.True(code.Contains("IL_0018: blt IL_0009"), "迴圈");
            Assert.True(code.Contains("IL_001d: ldstr \"Total = \""), "字串常值");
            Assert.True(code.Contains("IL_0023: box [mscorlib]System.Int32"), "型別運算元");
            Assert.True(code.Contains("IL_0028: call string [mscorlib]System.String::Concat(object, object)"), "外部方法");
            Assert.True(code.Contains("IL_002d: call void [mscorlib]System.Console::WriteLine(string)"), "Console.WriteLine");
            Assert.Equal("IL_0042: ret", code[code.Length - 1]);
            Assert.Equal(31, code.Length);
            Assert.Contains("ldstr \"Total = \"", main.Text);
        }

        [Test]
        public static void HelloCsTinyHeader()
        {
            var r = Disassemble("hello_cs.exe");
            var square = FindMethod(r, "AssamSample.Program", "Square");
            Assert.Equal("static int32 Square(int32)", square.Signature);
            Assert.Equal(4, square.CodeSize);
            Assert.Equal(8, square.MaxStack);
            Assert.Equal(0, square.LocalCount);
            Assert.Equal("IL_0000: ldarg.0|IL_0001: ldarg.0|IL_0002: mul|IL_0003: ret", string.Join("|", square.Lines));
        }

        [Test]
        public static void OwnTestAssemblyHasNoUnknownOpcodes()
        {
            string path = Assembly.GetExecutingAssembly().Location;
            var r = CilDisassembler.Disassemble(BinaryLoader.Load(path), 100000);
            Assert.Equal(0, r.Messages.Count, string.Join(" | ", r.Messages));
            Assert.Equal("Assam.Tests", r.AssemblyName);
            Assert.Equal("Assam.Tests.Program::Main", r.EntryPoint);
            Assert.True(r.MethodCount > 50, "方法數 " + r.MethodCount);
            Assert.True(r.AssemblyReferences.Any(a => a.StartsWith("Assam.Core ", StringComparison.Ordinal)), string.Join(", ", r.AssemblyReferences));
            int bodies = 0;
            foreach (var type in r.Types)
            {
                foreach (var m in type.Methods)
                {
                    Assert.NotNull(m.Signature, m.Name);
                    Assert.DoesNotContain("???", m.Signature, type.FullName + "::" + m.Name);
                    if (!m.HasBody) continue;
                    bodies++;
                    Assert.True(Code(m).Length > 0, "沒有指令：" + type.FullName + "::" + m.Name);
                    foreach (var line in m.Lines)
                    {
                        // 字串常值本身可能含有這些字（例如本檔案的斷言訊息），只檢查其他指令
                        if (line.Contains(": ldstr ")) continue;
                        Assert.DoesNotContain("未知的 IL 運算碼", line, type.FullName + "::" + m.Name);
                        Assert.DoesNotContain("token 0x", line, type.FullName + "::" + m.Name);
                        Assert.DoesNotContain("???", line, type.FullName + "::" + m.Name);
                        Assert.DoesNotContain("不完整", line, type.FullName + "::" + m.Name);
                    }
                }
            }
            Assert.True(bodies > 50);

            // 這個測試方法本身也在清單中，而且看得到對反組譯器的呼叫
            var self = FindMethod(r, "Assam.Tests.CilDisassemblerTests", "OwnTestAssemblyHasNoUnknownOpcodes");
            Assert.Equal("static void OwnTestAssemblyHasNoUnknownOpcodes()", self.Signature);
            Assert.True(self.Lines.Any(l => l.Contains("call [Assam.Core]Assam.Core.Binary.CilDisassemblyResult [Assam.Core]Assam.Core.Binary.CilDisassembler::Disassemble([Assam.Core]Assam.Core.Binary.LoadedBinary, int32)")),
                        string.Join("\n", self.Lines));
            Assert.True(self.Lines.Any(l => l.Contains("ldstr \"OwnTestAssemblyHasNoUnknownOpcodes\"")));
            Assert.True(self.Lines.Any(l => l.Contains("ldc.i4 100000")));
        }

        [Test]
        public static void CoreLibraryDecodesCleanly()
        {
            string path = typeof(CilDisassembler).Assembly.Location;
            var watch = Stopwatch.StartNew();
            var r = CilDisassembler.Disassemble(BinaryLoader.Load(path), 100000);
            Assert.Equal(0, r.Messages.Count, string.Join(" | ", r.Messages));
            Assert.Equal("Assam.Core", r.AssemblyName);
            Assert.Null(r.EntryPoint);
            Assert.True(r.MethodCount > 300, "方法數 " + r.MethodCount);
            Assert.True(watch.ElapsedMilliseconds < 20000, "太慢：" + watch.ElapsedMilliseconds + " ms");
            foreach (var m in r.Types.SelectMany(t => t.Methods))
                foreach (var line in m.Lines)
                {
                    if (line.Contains(": ldstr ")) continue;
                    Assert.DoesNotContain("未知的 IL 運算碼", line, m.TypeName + "::" + m.Name);
                    Assert.DoesNotContain("token 0x", line, m.TypeName + "::" + m.Name);
                }
            // 巢狀型別以 "Outer/Inner" 表示（CilFormatter 裡有巢狀的 MethodSig 類別）
            Assert.True(r.Types.Any(t => t.FullName == "Assam.Core.Binary.CilFormatter/MethodSig"),
                        string.Join(", ", r.Types.Select(t => t.FullName).Where(n => n.Contains("/")).Take(20)));
            var open = FindMethod(r, "Assam.Core.Binary.ClrMetadataReader", "Open");
            Assert.Equal("static Assam.Core.Binary.ClrMetadataReader Open(Assam.Core.Binary.LoadedBinary, [mscorlib]System.Collections.Generic.List`1<string>)", open.Signature);
        }

        [Test]
        public static void MaxMethodsTruncates()
        {
            var r = CilDisassembler.Disassemble(BinaryLoader.Load(typeof(CilDisassembler).Assembly.Location), 10);
            Assert.Equal(10, r.MethodCount);
            Assert.Equal(10, r.Types.Sum(t => t.Methods.Count));
            Assert.True(r.Truncated);
            Assert.True(r.Messages.Any(m => m.Contains("上限")), string.Join(" | ", r.Messages));

            var none = CilDisassembler.Disassemble(BinaryLoader.Load(BinaryLoaderTests.TestFile("hello_cs.exe")), 0);
            Assert.Equal(0, none.MethodCount);
            Assert.True(none.Truncated);
            Assert.Equal("AssamSample.Program::Main", none.EntryPoint);
        }

        [Test]
        public static void NonDotNetFileGivesFriendlyMessage()
        {
            foreach (var name in new[] { "hello32.exe", "mathlib64.dll", "dos_hello.com" })
            {
                var r = Disassemble(name);
                Assert.Equal(0, r.Types.Count, name);
                Assert.Equal(0, r.MethodCount, name);
                Assert.Null(r.EntryPoint, name);
                Assert.True(r.Messages.Count == 1, name);
                Assert.Contains("不是 .NET 組件", r.Messages[0], name);
            }
            var nothing = CilDisassembler.Disassemble(null);
            Assert.True(nothing.Messages.Count > 0);
        }

        [Test]
        public static void IlFeaturesSignatures()
        {
            var r = Disassemble("cil_features.dll");
            Assert.Equal(0, r.Messages.Count, string.Join(" | ", r.Messages));
            Assert.Equal("cil_features", r.AssemblyName);
            Assert.Equal("1.2.3.4", r.AssemblyVersion);
            Assert.Null(r.EntryPoint);

            const string sigs = "Demo.Sigs";
            Assert.Equal("static vararg int32 Sum(int32)", FindMethod(r, sigs, "Sum").Signature);
            Assert.Equal("static float64[0...2,1...] Bounded(int32[,], string[][], int32 modreq([mscorlib]System.Runtime.CompilerServices.IsConst)&, typedref)",
                         FindMethod(r, sigs, "Bounded").Signature);
            Assert.Equal("static void Prefixes(int32*)", FindMethod(r, sigs, "Prefixes").Signature);
            Assert.Equal("void .ctor()", FindMethod(r, sigs, ".ctor").Signature);
            Assert.Equal("!!0 Map<M>(!0, !!0, [mscorlib]System.Collections.Generic.List`1<!!0>)", FindMethod(r, "Demo.Box`1", "Map").Signature);

            // 全域函式放在 <Module>；巢狀型別以 "/" 連接
            Assert.Equal("static void GlobalFunc()", FindMethod(r, "<Module>", "GlobalFunc").Signature);
            var inner = r.Types.First(t => t.FullName == "Demo.Sigs/Inner");
            Assert.Equal("Demo", inner.Namespace);
            Assert.Equal("Sigs/Inner", inner.Name);

            // 介面的抽象方法沒有主體
            var area = FindMethod(r, "Demo.IShape", "Area");
            Assert.False(area.HasBody);
            Assert.Equal(0, area.Lines.Count);
            Assert.Contains("沒有 IL 主體", area.Text);

            var locals = FindMethod(r, sigs, "Locals");
            Assert.Equal(11, locals.LocalCount);
            Assert.True(locals.Lines[0].StartsWith("// .locals init (int32& V_0, native uint V_1, char V_2, bool V_3, uint8 V_4", StringComparison.Ordinal), locals.Lines[0]);
            Assert.True(locals.Lines.Contains("IL_0002: ldelema int32"));
        }

        [Test]
        public static void IlFeaturesOperands()
        {
            var r = Disassemble("cil_features.dll");
            const string sigs = "Demo.Sigs";
            var calli = Code(FindMethod(r, sigs, "CallIndirect"));
            Assert.True(calli.Contains("IL_0002: ldftn int32 Demo.Sigs::Add(int32, int32)"), string.Join("\n", calli));
            Assert.True(calli.Contains("IL_0008: calli int32(int32, int32)"), string.Join("\n", calli));

            var vararg = Code(FindMethod(r, sigs, "CallVararg"));
            Assert.True(vararg.Contains("IL_0001: ldc.i4.s -10"), string.Join("\n", vararg));
            Assert.True(vararg.Contains("IL_0003: ldc.r8 2.5"));
            Assert.True(vararg.Contains("IL_000c: call vararg int32 Demo.Sigs::Sum(int32, ..., int32, float64)"), string.Join("\n", vararg));

            var sum = FindMethod(r, sigs, "Sum");
            Assert.True(sum.Lines.Contains("IL_0004: call instance void [mscorlib]System.ArgIterator::.ctor([mscorlib]System.RuntimeArgumentHandle)"), string.Join("\n", sum.Lines));
            Assert.True(sum.Lines.Contains("IL_0002: arglist"), "兩位元組運算碼 0xFE 0x00");

            var p = Code(FindMethod(r, sigs, "Prefixes"));
            string all = string.Join("\n", p);
            Assert.True(p.Contains("IL_0001: unaligned. 1"), all);
            Assert.True(p.Contains("IL_0004: volatile."), all);
            Assert.True(p.Contains("IL_000b: initblk"), all);
            Assert.True(p.Contains("IL_000d: ldc.r4 -0.5"), all);
            Assert.True(p.Contains("IL_0013: ldc.r8 1E+300"), all);
            Assert.True(p.Contains("IL_001d: ldc.i8 -9000000000"), all);
            Assert.True(p.Contains("IL_0027: ldc.i4 -100000"), all);
            Assert.True(p.Contains("IL_002d: ldtoken method int32 Demo.Sigs::Add(int32, int32)"), all);
            Assert.True(p.Contains("IL_0033: ldtoken field int32 modopt([mscorlib]System.Runtime.CompilerServices.IsVolatile) Demo.Sigs::Counter"), all);
            Assert.True(p.Contains("IL_0039: ldsfld method int32 *(int32, float64) Demo.Sigs::FnField"), all);
            Assert.True(p.Contains("IL_003f: ldstr \"tab\\there \\\"quoted\\\" back\\\\slash\""), all);
            Assert.True(p.Contains("IL_0045: ldstr \"中文\\u0001\""), all);
            Assert.True(p.Contains("IL_004b: tail."), all);
            Assert.True(p.Contains("IL_004d: call void Demo.Sigs::Prefixes(int32*)"), all);

            var jump = Code(FindMethod(r, sigs, "Jump"));
            Assert.True(jump.Contains("IL_0001: switch (IL_0014, IL_0017, IL_001a)"), string.Join("\n", jump));
            Assert.True(jump.Contains("IL_0012: ldc.i4.m1"), string.Join("\n", jump));

            var useIt = Code(FindMethod(r, "Demo.Box`1", "UseIt"));
            all = string.Join("\n", useIt);
            Assert.True(useIt.Contains("IL_0000: newobj instance void Demo.Box`1<string>::.ctor()"), all);
            Assert.True(useIt.Contains("IL_000e: callvirt instance !!0 Demo.Box`1<string>::Map<int32>(!0, !!0, [mscorlib]System.Collections.Generic.List`1<!!0>)"), all);
            Assert.True(useIt.Contains("IL_0015: ldfld !0 Demo.Box`1<string>::Item"), all);
            Assert.True(useIt.Contains("IL_001b: call string Demo.Sigs/Inner::Hello()"), all);

            var catcher = FindMethod(r, sigs, "Catcher");
            Assert.True(catcher.Lines.Contains("IL_0000: call void GlobalFunc()"), string.Join("\n", catcher.Lines));
            Assert.True(catcher.Lines.Any(l => l.StartsWith("//") && l.Contains(".try IL_0000") && l.Contains("catch [mscorlib]System.InvalidOperationException") && l.Contains("IL_0007 到 IL_000a")),
                        string.Join("\n", catcher.Lines));

            var handlers = FindMethod(r, sigs, "Handlers");
            Assert.True(handlers.Lines.Any(l => l.StartsWith("//") && l.Contains("filter IL_0006")), string.Join("\n", handlers.Lines));
            Assert.True(handlers.Lines.Any(l => l.StartsWith("//") && l.Contains("fault")), string.Join("\n", handlers.Lines));
            Assert.True(handlers.Lines.Contains("IL_000f: endfinally"));
        }

        [Test]
        public static void TruncatedFilesNeverThrow()
        {
            var original = File.ReadAllBytes(BinaryLoaderTests.TestFile("hello_cs.exe"));
            int loaded = 0;
            for (int length = 0x200; length < original.Length; length += 7)
            {
                var data = new byte[length];
                Buffer.BlockCopy(original, 0, data, 0, length);
                LoadedBinary binary;
                try
                {
                    binary = BinaryLoader.Load(data, "truncated.exe");
                }
                catch (BinaryFormatException)
                {
                    continue;
                }
                loaded++;
                var r = CilDisassembler.Disassemble(binary);
                Assert.NotNull(r);
                AssertNoInternalErrors(r, "長度 " + length);
            }
            Assert.True(loaded > 100, "載入次數 " + loaded);
        }

        [Test]
        public static void CorruptedMetadataFuzzNeverThrows()
        {
            var originals = new[]
            {
                File.ReadAllBytes(BinaryLoaderTests.TestFile("hello_cs.exe")),
                File.ReadAllBytes(BinaryLoaderTests.TestFile("cil_features.dll"))
            };
            var random = new Random(20261014);
            var watch = Stopwatch.StartNew();
            int runs = 0, withMethods = 0;
            for (int i = 0; i < 4000; i++)
            {
                var original = originals[i % originals.Length];
                var data = (byte[])original.Clone();
                int mutations = 1 + random.Next(12);
                for (int k = 0; k < mutations; k++)
                {
                    // 大多數變動集中在 .text 區段（CLR 標頭、中繼資料、方法主體都在這裡）
                    int at = random.Next(4) == 0 ? random.Next(data.Length) : 0x200 + random.Next(Math.Max(1, data.Length - 0x200));
                    switch (random.Next(5))
                    {
                        case 0: data[at] = 0xFF; break;
                        case 1: data[at] = 0x00; break;
                        case 2: data[at] ^= (byte)(1 << random.Next(8)); break;
                        default: data[at] = (byte)random.Next(256); break;
                    }
                }
                LoadedBinary binary;
                try
                {
                    binary = BinaryLoader.Load(data, "fuzz.exe");
                }
                catch (BinaryFormatException)
                {
                    continue;
                }
                var r = CilDisassembler.Disassemble(binary, 1000);
                runs++;
                if (r.MethodCount > 0) withMethods++;
                AssertNoInternalErrors(r, "第 " + i + " 次");
                foreach (var m in r.Types.SelectMany(t => t.Methods))
                {
                    Assert.NotNull(m.Signature);
                    Assert.NotNull(m.Text);
                }
            }
            Assert.True(runs > 2000, "成功載入 " + runs + " 次");
            Assert.True(withMethods > 1000, "有方法的結果 " + withMethods + " 次");
            Assert.True(watch.ElapsedMilliseconds < 60000, "模糊測試太慢：" + watch.ElapsedMilliseconds + " ms");
        }
    }
}
