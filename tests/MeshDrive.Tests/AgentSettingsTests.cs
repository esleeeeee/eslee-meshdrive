using MeshDrive.Agent;

namespace MeshDrive.Tests;

[TestClass]
public sealed class AgentSettingsTests
{
    [TestMethod]
    public void SettingsPersistDeviceNamePauseAndOnboarding()
    {
        using var fixture = new StorageTests.StorageFixture();
        var settings = new AgentSettings { DeviceName = "My PC", SharingPaused = true, OnboardingComplete = true };
        settings.Save(fixture.Data);
        var loaded = AgentSettings.Load(fixture.Data);
        Assert.AreEqual("My PC", loaded.DeviceName); Assert.IsTrue(loaded.SharingPaused); Assert.IsTrue(loaded.OnboardingComplete);
        StringAssert.StartsWith(TrayFolderConnection.PipeName, "eslee.trayfolder.tray-host.v1.");
    }
}
