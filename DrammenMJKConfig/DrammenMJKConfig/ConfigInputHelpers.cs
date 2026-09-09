namespace DrammenMJKConfig;

internal static class ConfigInputHelpers
{
    internal const char EscKey = '\x1B';

    internal static char ReadKey() => Console.ReadKey(intercept: true).KeyChar;

    internal static char ReadChar(Func<char, bool> valid)
    {
        while (true) { char c = ReadKey(); if (valid(c)) return c; }
    }

    // Console.ReadLine() can't observe Escape at all (it's line-buffered and
    // Escape doesn't submit the line), so prompts built on it could only ever
    // treat blank+Enter as "go back", not Esc. This reads key-by-key instead
    // (same primitive as ReadChar), building up a string, so Esc and blank
    // both mean the same thing everywhere: null, i.e. go back. Backspace
    // edits normally; Enter submits.
    internal static string? ReadLineOrEsc()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape) { Console.WriteLine("[Esc]"); return null; }
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
                continue;
            }
            if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }
        string result = sb.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    internal static string BuildRemaining(char first, char last, bool[] assigned)
    {
        var sb = new System.Text.StringBuilder("[");
        bool any = false;
        for (char k = first; k <= last; k++)
        {
            if (!assigned[k - first]) { if (any) sb.Append(','); sb.Append(k); any = true; }
        }
        sb.Append(']');
        return any ? sb.ToString() : "[none]";
    }

    internal static string BuildRemainingLeds(int numLeds, bool[] usedLed)
    {
        var sb = new System.Text.StringBuilder("[");
        bool any = false;
        for (int i = 0; i < numLeds; i++)
        {
            if (!usedLed[i]) { if (any) sb.Append(','); sb.Append(i); any = true; }
        }
        sb.Append(']');
        return any ? sb.ToString() : "[none]";
    }

    internal static bool AllTrue(bool[] arr)
    {
        foreach (bool b in arr) if (!b) return false;
        return true;
    }
}
