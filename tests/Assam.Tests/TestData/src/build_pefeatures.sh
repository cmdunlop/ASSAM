#!/bin/sh
# 產生 pefeatures32.dll / pefeatures64.dll（需要 clang-cl、lld-link、llvm-dlltool；不需要 Windows SDK 或 C 執行階段）。
# 用來測試載入器：延遲載入、序號匯入、轉送匯出、別名匯出、只有序號的匯出、TLS 回呼。
# 獨立於 build.sh（需要 LLVM 工具），重新產生時請執行：sh build_pefeatures.sh
set -e
cd "$(dirname "$0")"
O=..
T=$(mktemp -d)
for arch in x86 x64; do
    if [ $arch = x86 ]; then m=i386; target=i686-pc-windows-msvc; out=pefeatures32.dll; tls=__tls_used; else m=i386:x86-64; target=x86_64-pc-windows-msvc; out=pefeatures64.dll; tls=_tls_used; fi
    llvm-dlltool -m $m -k -d kernel32_$arch.def -l $T/kernel32.lib
    llvm-dlltool -m $m -k -d user32_$arch.def -l $T/user32.lib
    llvm-dlltool -m $m -d ordlib.def -l $T/ordlib.lib
    clang-cl-18 --target=$target /c /O1 /GS- /Zl /Fo$T/pefeatures.obj pefeatures.c
    lld-link /dll /nodefaultlib /entry:DllMain /opt:noref /noimplib /out:$O/$out \
        $T/pefeatures.obj $T/kernel32.lib $T/user32.lib $T/ordlib.lib \
        /delayload:user32.dll /include:$tls \
        /export:FwdSleep=kernel32.Sleep /export:AliasAdd=AddTwo /export:HiddenByOrdinal,@20,NONAME
done
rm -rf $T
