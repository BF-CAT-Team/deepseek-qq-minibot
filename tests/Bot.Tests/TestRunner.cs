using System.Reflection;
using Xunit;

namespace Bot.Tests;

/// <summary>
/// 自带的最小测试运行器。
/// <para>为什么不用 <c>dotnet test</c>：vstest 的 testhost 需要打开父进程句柄，
/// 在受限令牌/沙箱环境下会直接抛 "拒绝访问"，整个测试运行中止。这个运行器只做反射调用，
/// 零外部依赖，双击或 <c>dotnet run</c> 即可跑完全部断言。</para>
/// <para>测试仍按 xUnit 的 <c>[Fact]</c> / <c>[Theory]</c> / <c>[InlineData]</c> 编写，
/// 在有完整权限的机器上依然可以用 <c>dotnet test</c> 跑。</para>
/// </summary>
internal static class TestRunner
{
    public static int Main(string[] args)
    {
        var filter = args.FirstOrDefault(a => !a.StartsWith('-'));

        var assembly = typeof(TestRunner).Assembly;
        var results = new List<(string Name, bool Passed, string? Error)>();
        var start = DateTime.UtcNow;

        foreach (var type in assembly.GetTypes()
                     .Where(t => t.IsClass && !t.IsAbstract && t.Namespace == "Bot.Tests")
                     .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                         .OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                var fact = method.GetCustomAttribute<FactAttribute>();
                var inlineData = method.GetCustomAttributes<InlineDataAttribute>().ToArray();
                if (fact is null && inlineData.Length == 0)
                {
                    continue;
                }

                if (fact?.Skip is not null)
                {
                    continue;
                }

                var name = $"{type.Name}.{method.Name}";
                if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var cases = inlineData.Length > 0
                    ? inlineData.SelectMany(attribute => attribute.GetData(method)).ToArray()
                    : new[] { Array.Empty<object>() };

                foreach (var caseData in cases)
                {
                    var display = inlineData.Length > 0
                        ? $"{name}({string.Join(", ", caseData.Select(Format))})"
                        : name;

                    try
                    {
                        var instance = method.IsStatic ? null : Activator.CreateInstance(type);
                        var result = method.Invoke(instance, caseData);
                        if (result is Task task)
                        {
                            task.GetAwaiter().GetResult();
                        }
                        else if (result is ValueTask valueTask)
                        {
                            valueTask.AsTask().GetAwaiter().GetResult();
                        }

                        (instance as IDisposable)?.Dispose();
                        results.Add((display, true, null));
                        Console.WriteLine("  PASS  " + display);
                    }
                    catch (Exception ex)
                    {
                        var inner = ex is TargetInvocationException { InnerException: { } real } ? real : ex;
                        results.Add((display, false, inner.ToString()));
                        Console.WriteLine("  FAIL  " + display);
                        Console.WriteLine("        " + inner.GetType().Name + ": " + inner.Message);
                    }
                }
            }
        }

        var failed = results.Count(r => !r.Passed);
        var elapsed = DateTime.UtcNow - start;

        Console.WriteLine();
        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"总计 {results.Count} 项｜通过 {results.Count - failed}｜失败 {failed}｜耗时 {elapsed.TotalSeconds:F1}s");

        if (failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败详情：");
            foreach (var item in results.Where(r => !r.Passed))
            {
                Console.WriteLine();
                Console.WriteLine("### " + item.Name);
                Console.WriteLine(item.Error);
            }
        }

        return failed == 0 ? 0 : 1;
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        string text => "\"" + text + "\"",
        _ => value.ToString() ?? string.Empty,
    };
}
