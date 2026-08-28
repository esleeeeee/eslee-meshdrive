using MeshDrive.Core;

namespace MeshDrive.Tests;

[TestClass]
public sealed class SasCalculatorTests
{
    [TestMethod]
    public void SameTranscriptYieldsSameSixDigitSasRegardlessOfLocalOrder()
    {
        var left = new PairingSide("aaa", "AA" + new string('1', 62), "N1");
        var right = new PairingSide("bbb", "BB" + new string('2', 62), "N2");
        var first = PairingTranscript.Create(left, right);
        var second = PairingTranscript.Create(right, left);
        Assert.AreEqual(first.CanonicalText(), second.CanonicalText());
        var sas = SasCalculator.Compute(first);
        Assert.AreEqual(6, sas.Length);
        Assert.AreEqual(sas, SasCalculator.Compute(second));
        Assert.AreEqual($"{sas[..3]} {sas[3..]}", SasCalculator.FormatDisplay(sas));
    }

    [TestMethod]
    public void DifferentNonceChangesSas()
    {
        var a = new PairingSide("aaa", new string('A', 64), "N1");
        var b1 = new PairingSide("bbb", new string('B', 64), "N2");
        var b2 = new PairingSide("bbb", new string('B', 64), "N3");
        Assert.AreNotEqual(
            SasCalculator.Compute(PairingTranscript.Create(a, b1)),
            SasCalculator.Compute(PairingTranscript.Create(a, b2)));
    }
}
