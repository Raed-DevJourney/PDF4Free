using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace PDF4Free.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class CheckAttribute : Attribute;

public sealed partial class TestRunner(string outDir, string[] filters)
{
    public int Failed { get; private set; }
    public int Passed { get; private set; }
    public string OutDir { get; } = outDir;

    public void RunAll()
    {
        var methods = GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.GetCustomAttribute<CheckAttribute>() is not null)
            .Where(m => filters.Length == 0 || filters.Any(f => m.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m => m.MetadataToken);
        foreach (var method in methods)
        {
            try
            {
                var result = method.Invoke(this, null);
                if (result is Task task) task.GetAwaiter().GetResult();
                Passed++;
                Console.WriteLine($"PASS  {method.Name}");
            }
            catch (Exception ex)
            {
                Failed++;
                var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
                Console.WriteLine($"FAIL  {method.Name}: {inner.GetType().Name}: {inner.Message}");
                Console.WriteLine(inner.StackTrace);
            }
        }
        Console.WriteLine($"--- {Passed} passed, {Failed} failed");
    }

    public static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    public static void AssertEqual<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{what}: expected <{expected}> but was <{actual}>");
    }

    public string PathFor(string name) => Path.Combine(OutDir, name);

    public void SavePng(BitmapSource bitmap, string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(PathFor(name));
        encoder.Save(stream);
    }
}
