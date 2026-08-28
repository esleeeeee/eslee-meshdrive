using MeshDrive.Agent;
using MeshDrive.Core;

namespace MeshDrive.Tests;

[TestClass]
public sealed class AgentArgumentsTests
{
    [TestMethod]
    public void ParseUsesDefaultNamesWhenArgsAreEmpty()
    {
        Assert.IsTrue(AgentArguments.TryParse([], out var options, out var error));
        Assert.AreEqual(string.Empty, error);
        Assert.AreEqual(IpcNames.DefaultPipeName, options.PipeName);
        Assert.AreEqual(IpcNames.DefaultMutexName, options.MutexName);
        Assert.AreEqual(AppPaths.DefaultDataDirectory, options.DataDirectory);
        Assert.IsTrue(options.EnableMdns);
        Assert.IsTrue(options.EnableHttps);
        Assert.AreEqual(DiscoveryNames.DefaultPort, options.HttpsPort);
    }

    [TestMethod]
    public void ParseReadsPipeAndMutexOverrides()
    {
        Assert.IsTrue(AgentArguments.TryParse(
            ["--pipe-name", "mdt-pipe", "--mutex-name", @"Local\mdt-mutex"],
            out var options,
            out var error));
        Assert.AreEqual(string.Empty, error);
        Assert.AreEqual("mdt-pipe", options.PipeName);
        Assert.AreEqual(@"Local\mdt-mutex", options.MutexName);
        Assert.IsTrue(options.EnableMdns);
    }

    [TestMethod]
    public void ParseReadsDataDirectoryAndDisableMdns()
    {
        Assert.IsTrue(AgentArguments.TryParse(
            ["--data-dir", @"C:\tmp\md", "--disable-mdns"],
            out var options,
            out var error));
        Assert.AreEqual(string.Empty, error);
        Assert.AreEqual(@"C:\tmp\md", options.DataDirectory);
        Assert.IsFalse(options.EnableMdns);
    }

    [TestMethod]
    public void ParseRejectsUnknownOrIncompleteArgs()
    {
        Assert.IsFalse(AgentArguments.TryParse(["--nope"], out _, out var unknown));
        StringAssert.Contains(unknown, "알 수 없는 인수");
        Assert.IsFalse(AgentArguments.TryParse(["--pipe-name"], out _, out var missing));
        StringAssert.Contains(missing, "--pipe-name");
    }
}
