namespace Ioxide.E2E;

/// <summary>Tiny test runner: PASS / FAIL / SKIP per test, a summary line, and a non-zero exit on failure.</summary>
internal sealed class Runner
{
    private int _passed;
    private int _failed;
    private int _skipped;

    public void Test(string name, Action body, bool skip = false)
    {
        if (skip)
        {
            Console.WriteLine($"SKIP  {name}");
            _skipped++;
            return;
        }

        try
        {
            body();
            Console.WriteLine($"PASS  {name}");
            _passed++;
        }
        catch (Exception e)
        {
            Console.WriteLine($"FAIL  {name}: {e.Message}");
            _failed++;
        }
    }

    public int Summary()
    {
        Console.WriteLine($"\n{_passed} passed, {_failed} failed, {_skipped} skipped");
        return _failed == 0 ? 0 : 1;
    }
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual))
        {
            throw new Exception($"expected [{expected}], got [{actual}]");
        }
    }

    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
}
