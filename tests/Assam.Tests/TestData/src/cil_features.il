// ASSAM 測試用：IL 反組譯器的簽章與指令特例（ilasm /dll /output:cil_features.dll cil_features.il）
// 涵蓋 C# 編譯器不會產生的寫法：calli、vararg（SENTINEL）、modreq/modopt、函式指標（FNPTR）、
// 有界限的多維陣列、filter/fault 例外處理、全域函式、巢狀型別、switch、no./unaligned./volatile./tail. 前綴。
.assembly extern mscorlib { .publickeytoken = (B7 7A 5C 56 19 34 E0 89) .ver 4:0:0:0 }
.assembly cil_features { .ver 1:2:3:4 }
.module cil_features.dll

.namespace Demo
{
  .class public auto ansi beforefieldinit Sigs extends [mscorlib]System.Object
  {
    .field public static int32 modopt([mscorlib]System.Runtime.CompilerServices.IsVolatile) Counter
    .field public static method int32 *(int32, float64) FnField

    .method public static int32 Add(int32 a, int32 b) cil managed
    {
      .maxstack 2
      ldarg.0
      ldarg.1
      add
      ret
    }

    .method public static int32 CallIndirect(int32 x) cil managed
    {
      .maxstack 3
      ldarg.0
      ldc.i4.7
      ldftn int32 Demo.Sigs::Add(int32, int32)
      calli int32(int32, int32)
      ret
    }

    .method public static vararg int32 Sum(int32 n) cil managed
    {
      .maxstack 2
      .locals init (valuetype [mscorlib]System.ArgIterator it)
      ldloca.s it
      arglist
      call instance void [mscorlib]System.ArgIterator::.ctor(valuetype [mscorlib]System.RuntimeArgumentHandle)
      ldarg.0
      ret
    }

    .method public static int32 CallVararg() cil managed
    {
      .maxstack 4
      ldc.i4.2
      ldc.i4.s -10
      ldc.r8 2.5
      call vararg int32 Demo.Sigs::Sum(int32, ..., int32, float64)
      ret
    }

    .method public static float64[0...2, 1...] Bounded(int32[,] grid, string[][] jag, int32 modreq([mscorlib]System.Runtime.CompilerServices.IsConst)& r, typedref tr) cil managed
    {
      .maxstack 1
      ldnull
      ret
    }

    .method public static native int Locals(int32[] arr) cil managed
    {
      .maxstack 2
      .locals init (int32& p, native uint q, char c, bool b, uint8 u8, int16 s16, uint16 u16, uint32 u32, uint64 u64, float32 f32, object o)
      ldarg.0
      ldc.i4.0
      ldelema [mscorlib]System.Int32
      stloc.0
      ldloc.0
      conv.i
      ret
    }

    .method public static void Prefixes(int32* p) cil managed
    {
      .maxstack 3
      ldarg.0
      unaligned. 1
      volatile.
      ldind.i4
      pop
      ldarg.0
      ldc.i4.0
      ldc.i4.4
      initblk
      ldc.r4 -0.5
      pop
      ldc.r8 1e300
      pop
      ldc.i8 -9000000000
      pop
      ldc.i4 -100000
      pop
      ldtoken method int32 Demo.Sigs::Add(int32, int32)
      pop
      ldtoken field int32 modopt([mscorlib]System.Runtime.CompilerServices.IsVolatile) Demo.Sigs::Counter
      pop
      ldsfld method int32 *(int32, float64) Demo.Sigs::FnField
      pop
      ldstr "tab\there \"quoted\" back\\slash"
      pop
      ldstr bytearray (2D 4E 87 65 01 00)
      pop
      tail.
      call void Demo.Sigs::Prefixes(int32*)
      ret
    }

    .method public static int32 Jump(int32 x) cil managed
    {
      .maxstack 1
      ldarg.0
      switch (L0, L1, L2)
      ldc.i4.m1
      ret
    L0:
      ldc.i4.s 10
      ret
    L1:
      ldc.i4.s 20
      ret
    L2:
      ldc.i4.s 30
      ret
    }

    .method public static int32 Handlers(int32 x) cil managed
    {
      .maxstack 2
      .locals init (int32 r)
      .try
      {
        .try
        {
          ldarg.0
          ldc.i4.0
          div
          stloc.0
          leave.s L_END
        }
        filter
        {
          pop
          ldc.i4.1
          endfilter
        }
        {
          pop
          ldc.i4.m1
          stloc.0
          leave.s L_END
        }
      }
      fault
      {
        endfault
      }
    L_END:
      ldloc.0
      ret
    }

    .method public static void Catcher() cil managed
    {
      .maxstack 1
      .try
      {
        call void GlobalFunc()
        leave.s DONE
      }
      catch [mscorlib]System.InvalidOperationException
      {
        pop
        leave.s DONE
      }
    DONE:
      ret
    }

    .method public hidebysig specialname rtspecialname instance void .ctor() cil managed
    {
      .maxstack 1
      ldarg.0
      call instance void [mscorlib]System.Object::.ctor()
      ret
    }

    .class nested public auto ansi beforefieldinit Inner extends [mscorlib]System.Object
    {
      .method public static string Hello() cil managed
      {
        .maxstack 1
        ldstr "inner"
        ret
      }
    }
  }

  .class public auto ansi beforefieldinit Box`1<T> extends [mscorlib]System.Object
  {
    .field public !T Item
    .method public hidebysig instance !!M Map<M>(!T t, !!M m, class [mscorlib]System.Collections.Generic.List`1<!!M> lst) cil managed
    {
      .maxstack 1
      ldarg.2
      ret
    }
    .method public hidebysig static void UseIt() cil managed
    {
      .maxstack 4
      .locals init (class Demo.Box`1<string> b)
      newobj instance void class Demo.Box`1<string>::.ctor()
      stloc.0
      ldloc.0
      ldstr "s"
      ldc.i4.3
      ldnull
      callvirt instance !!0 class Demo.Box`1<string>::Map<int32>(!0, !!0, class [mscorlib]System.Collections.Generic.List`1<!!0>)
      pop
      ldloc.0
      ldfld !0 class Demo.Box`1<string>::Item
      pop
      call string Demo.Sigs/Inner::Hello()
      pop
      ret
    }
    .method public hidebysig specialname rtspecialname instance void .ctor() cil managed
    {
      .maxstack 1
      ldarg.0
      call instance void [mscorlib]System.Object::.ctor()
      ret
    }
  }

  .class interface public abstract auto ansi IShape
  {
    .method public hidebysig newslot abstract virtual instance float64 Area() cil managed
    {
    }
  }
}

.method public static void GlobalFunc() cil managed
{
  .maxstack 1
  ret
}
