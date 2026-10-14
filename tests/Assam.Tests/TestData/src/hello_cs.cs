// ASSAM 測試用：.NET 主控台程式（mcs hello_cs.cs -out:hello_cs.exe）
using System;

namespace AssamSample
{
    public static class Program
    {
        private static int Square(int x)
        {
            return x * x;
        }

        public static int Main(string[] args)
        {
            int total = 0;
            for (int i = 0; i < 5; i++) total += Square(i);
            Console.WriteLine("Total = " + total);
            return args.Length > 0 ? 1 : 0;
        }
    }
}
