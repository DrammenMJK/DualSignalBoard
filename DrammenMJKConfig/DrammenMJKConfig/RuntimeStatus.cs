using System.Globalization;

namespace DrammenMJKConfig;

// The firmware runtime loop's state, from "RT": "RT <0|1> <pens error mask hex> <dreieskive error 0|1>".
// PensErrors has one bit per switch-table slot whose feedback reads 00 (both contacts closed).
sealed record RuntimeStatus(bool On, uint PensErrors, bool DreieskiveError)
{
    public static RuntimeStatus? Parse(string? line)
    {
        var parts = line?.Split(' ');
        if (parts is not ["RT", var on, var mask, var drei]
            || on is not ("0" or "1") || drei is not ("0" or "1")
            || !uint.TryParse(mask, NumberStyles.HexNumber, null, out uint errors))
            return null;
        return new RuntimeStatus(on == "1", errors, drei == "1");
    }

    public IEnumerable<int> PensErrorSlots => Enumerable.Range(0, 32).Where(slot => (PensErrors >> slot & 1) == 1);
}
