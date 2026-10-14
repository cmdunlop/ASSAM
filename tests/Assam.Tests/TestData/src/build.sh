#!/bin/sh
# 重新產生測試用二進位檔（需要 mingw-w64、nasm、mono-mcs）。產物放在上一層 TestData/。
set -e
cd "$(dirname "$0")"
O=..
i686-w64-mingw32-gcc   -O1 -s -o $O/hello32.exe hello.c
x86_64-w64-mingw32-gcc -O1 -s -o $O/hello64.exe hello.c
i686-w64-mingw32-gcc   -O2 -s -shared -o $O/mathlib32.dll mathlib.c
x86_64-w64-mingw32-gcc -O2 -s -shared -o $O/mathlib64.dll mathlib.c
nasm -f bin dos_hello_com.asm -o $O/dos_hello.com
nasm -f bin dos_hello_mz.asm  -o $O/dos_hello_mz.exe
mcs -nologo -out:$O/hello_cs.exe hello_cs.cs
