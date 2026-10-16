namespace DrammenMJKConfig;

internal static class ConfigInputHelpers
{
    internal const char EscKey = '\x1B';

    internal static char ReadKey() => Console.ReadKey(intercept: true).KeyChar;

    internal static char ReadChar(Func<char, bool> valid)
    {
        while (true) { char c = ReadKey(); if (valid(c)) return c; }
    }

    // Single-key choices for lists longer than 9: hexadecimal digits.
    internal const string ZeroBasedKeys = "0123456789ABCDEF", OneBasedKeys = "123456789ABCDEFG";

    // Index picked by key, or -1 on Esc.
    internal static int ReadKeyIndex(int count, string keys = ZeroBasedKeys)
    {
        char c = char.ToUpper(ReadChar(ch => ch == EscKey || keys.IndexOf(char.ToUpper(ch)) is int i && i >= 0 && i < count));
        Console.WriteLine(c == EscKey ? "[Esc]" : c.ToString());
        return c == EscKey ? -1 : keys.IndexOf(c);
    }

    // "Boards: 1=X  2=Y" prompt. Index of the chosen board, or -1 on Esc.
    internal static int PickBoard(IEnumerable<string> names)
    {
        Console.WriteLine("Boards: " + string.Join("  ", names.Select((n, i) => $"{OneBasedKeys[i]}={n}")));
        Console.Write("Select board (or Esc): ");
        return ReadKeyIndex(names.Count(), OneBasedKeys);
    }

    // Board picker over this SVB's SCBs. Null on Esc.
    internal static BoardScb? PickScb()
    {
        var scbs = BoardConfig.Svb.Scbs;
        int i = PickBoard(scbs.Select(b => b.Name));
        if (i < 0) Console.WriteLine();
        return i < 0 ? null : scbs[i];
    }
}
