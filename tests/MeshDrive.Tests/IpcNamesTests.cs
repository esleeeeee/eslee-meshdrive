using MeshDrive.Core;

namespace MeshDrive.Tests;

[TestClass]
public sealed class IpcNamesTests
{
    [TestMethod]
    public void SanitizeKeepsAsciiAndReplacesOtherCharacters()
    {
        Assert.AreEqual("eslee-user_1", IpcNames.Sanitize("eslee-user_1"));
        Assert.AreEqual("dldms", IpcNames.Sanitize("dldms"));
        Assert.AreEqual("user-name", IpcNames.Sanitize("user name"));
        Assert.AreEqual("user", IpcNames.Sanitize(string.Empty));
    }

    [TestMethod]
    public void PipeAndMutexNamesAreUserScoped()
    {
        var pipe = IpcNames.BuildPipeName("dldms");
        var mutex = IpcNames.BuildMutexName("dldms");
        Assert.AreEqual("eslee.meshdrive.agent.v1.dldms", pipe);
        Assert.AreEqual(@"Local\eslee.meshdrive.agent.v1.dldms", mutex);
        StringAssert.StartsWith(pipe, "eslee.meshdrive.agent.v1.");
        StringAssert.StartsWith(mutex, @"Local\eslee.meshdrive.agent.v1.");
    }
}
