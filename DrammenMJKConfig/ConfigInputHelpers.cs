namespace DrammenMJKConfig;

internal static class ConfigInputHelpers
{
    internal const char EscKey = '\x1B';

    internal static char ReadKey() => Console.ReadKey(intercept: true).KeyChar;

    internal static char ReadChar(Func<char, bool> valid)
    {
        while (true) { char c = ReadKey(); if (valid(c)) return c; }
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
