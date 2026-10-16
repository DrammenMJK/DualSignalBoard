namespace DrammenMJKConfig.Tests;

// Motor scan's mapping from one observed move to polarity + feedback bits.
// Pair (2,3); after the move bit 3 is high, i.e. bit 3 is the contact for
// the position the motor just reached.
public class FeedbackMappingTests
{
    [TestCase(true, true, 1, 3, 2, TestName = "Fired high, ended at Rett")]
    [TestCase(false, true, 0, 3, 2, TestName = "Fired low, ended at Rett")]
    [TestCase(true, false, 0, 2, 3, TestName = "Fired high, ended at Avvik")]
    [TestCase(false, false, 1, 2, 3, TestName = "Fired low, ended at Avvik")]
    public void FeedbackMapping_PolarityIsTheOlatValueForRett(bool fireState, bool atRett, int polarity, int rettBit, int avvikBit)
    {
        var m = ConfigSession.FeedbackMapping(fireState, atRett, pairLow: 2, changed: 0b0000_1000);

        Assert.That(m, Is.EqualTo(((byte)polarity, (byte)rettBit, (byte)avvikBit)));
    }
}
