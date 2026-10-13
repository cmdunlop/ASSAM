using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace Assam.Tests
{
    /// <summary>標記測試方法（public static void、無參數）。</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestAttribute : Attribute
    {
    }

    public sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message)
        {
        }
    }

    /// <summary>極簡斷言工具（不依賴任何 NuGet 套件，離線即可建置）。</summary>
    public static class Assert
    {
        public static void True(bool condition, string message = null)
        {
            if (!condition) throw new AssertionException(message ?? "預期為 true");
        }

        public static void False(bool condition, string message = null)
        {
            if (condition) throw new AssertionException(message ?? "預期為 false");
        }

        public static void Equal<T>(T expected, T actual, string message = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new AssertionException((message != null ? message + "：" : string.Empty) + "預期 <" + expected + ">，實際 <" + actual + ">");
        }

        public static void NotNull(object value, string message = null)
        {
            if (value == null) throw new AssertionException(message ?? "預期不是 null");
        }

        public static void Null(object value, string message = null)
        {
            if (value != null) throw new AssertionException((message ?? "預期是 null") + "，實際 <" + value + ">");
        }

        public static void Contains(string expectedSubstring, string actual, string message = null)
        {
            if (actual == null || actual.IndexOf(expectedSubstring, StringComparison.Ordinal) < 0)
                throw new AssertionException((message != null ? message + "：" : string.Empty) + "找不到「" + expectedSubstring + "」於：" + Shorten(actual));
        }

        public static void DoesNotContain(string unexpected, string actual, string message = null)
        {
            if (actual != null && actual.IndexOf(unexpected, StringComparison.Ordinal) >= 0)
                throw new AssertionException((message != null ? message + "：" : string.Empty) + "不應包含「" + unexpected + "」：" + Shorten(actual));
        }

        public static T Throws<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T ex)
            {
                return ex;
            }
            throw new AssertionException("預期拋出 " + typeof(T).Name);
        }

        private static string Shorten(string s)
        {
            if (s == null) return "(null)";
            return s.Length > 600 ? s.Substring(0, 600) + "…" : s;
        }
    }

    public static class TestRunner
    {
        public static int RunAll(string filter)
        {
            var methods = typeof(TestRunner).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(m => m.GetCustomAttributes(typeof(TestAttribute), false).Length > 0)
                .OrderBy(m => m.DeclaringType.Name).ThenBy(m => m.Name)
                .ToList();
            if (!string.IsNullOrEmpty(filter))
                methods = methods.Where(m => (m.DeclaringType.Name + "." + m.Name).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            int passed = 0, failed = 0;
            var total = Stopwatch.StartNew();
            foreach (var m in methods)
            {
                string name = m.DeclaringType.Name + "." + m.Name;
                var sw = Stopwatch.StartNew();
                try
                {
                    m.Invoke(null, null);
                    passed++;
                    Console.WriteLine("  PASS  " + name + " (" + sw.ElapsedMilliseconds + " ms)");
                }
                catch (TargetInvocationException ex)
                {
                    failed++;
                    var inner = ex.InnerException ?? ex;
                    Console.WriteLine("  FAIL  " + name);
                    Console.WriteLine("        " + inner.GetType().Name + ": " + inner.Message);
                    if (!(inner is AssertionException)) Console.WriteLine(inner.StackTrace);
                }
            }
            Console.WriteLine();
            Console.WriteLine("結果：通過 " + passed + "，失敗 " + failed + "，共 " + methods.Count + " 項，耗時 " + total.ElapsedMilliseconds + " ms");
            return failed == 0 ? 0 : 1;
        }
    }
}
