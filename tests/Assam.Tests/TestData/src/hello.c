/* ASSAM 測試用：簡單的主控台程式（i686/x86_64 MinGW 編譯成 hello32.exe / hello64.exe） */
#include <stdio.h>
#include <string.h>

int global_counter = 7;
const char *greeting = "Hello, ASSAM!";

__attribute__((noinline)) static int sum_to(int n)
{
    int s = 0;
    for (int i = 1; i <= n; i++) s += i;
    return s;
}

int main(int argc, char **argv)
{
    char buf[64];
    strcpy(buf, greeting);
    printf("%s %d\n", buf, sum_to(argc + 9));
    global_counter += argc;
    return global_counter > 100 ? 1 : 0;
}
