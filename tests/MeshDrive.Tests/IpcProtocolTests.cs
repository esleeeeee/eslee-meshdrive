using MeshDrive.Protocol;

namespace MeshDrive.Tests;

[TestClass]
public sealed class IpcProtocolTests
{
    [TestMethod]
    public void SerializeRoundTripsKnownFieldsAndIgnoresUnknownFields()
    {
        var payload = IpcProtocol.Serialize(new IpcMessage
        {
            Type = IpcProtocol.TypeStatus,
            ProtocolVersion = IpcProtocol.Version,
            Id = 7,
            ProcessId = 1234,
            State = IpcProtocol.StateRunning,
            Version = "0.0.1",
            SessionId = "abc",
            ClientCount = 1,
            StartedAt = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero),
            UptimeSeconds = 42,
        });

        StringAssert.Contains(payload, "\"type\":\"status\"");
        StringAssert.Contains(payload, "\"processId\":1234");
        Assert.IsNull(IpcProtocol.TryDeserialize(string.Empty));
        Assert.IsNull(IpcProtocol.TryDeserialize("{not json"));

        var restored = IpcProtocol.TryDeserialize(payload);
        Assert.IsNotNull(restored);
        Assert.AreEqual(IpcProtocol.TypeStatus, restored.Type);
        Assert.AreEqual(1234, restored.ProcessId);
        Assert.AreEqual(42, restored.UptimeSeconds);

        var withUnknown = IpcProtocol.TryDeserialize(
            """{"type":"hello","protocolVersion":1,"futureField":true,"nested":{"x":1}}""");
        Assert.IsNotNull(withUnknown);
        Assert.AreEqual(IpcProtocol.TypeHello, withUnknown.Type);
        Assert.AreEqual(1, withUnknown.ProtocolVersion);
        Assert.IsTrue(IpcProtocol.TryValidateHello(withUnknown, out var error));
        Assert.AreEqual(string.Empty, error);
    }

    [TestMethod]
    public void HelloValidationRejectsWrongTypeAndVersion()
    {
        Assert.IsFalse(IpcProtocol.TryValidateHello(new IpcMessage { Type = IpcProtocol.TypeGetStatus, ProtocolVersion = 1 }, out var typeError));
        StringAssert.Contains(typeError, "hello");

        Assert.IsFalse(IpcProtocol.TryValidateHello(new IpcMessage { Type = IpcProtocol.TypeHello, ProtocolVersion = 2 }, out var versionError));
        StringAssert.Contains(versionError, "버전");
    }

    [TestMethod]
    public void ToStatusUsesDefaultsForMissingFields()
    {
        var status = IpcProtocol.ToStatus(new IpcMessage { Type = IpcProtocol.TypeStatus });
        Assert.AreEqual(IpcProtocol.StateRunning, status.State);
        Assert.AreEqual(0, status.ProcessId);
        Assert.AreEqual(IpcProtocol.Version, status.ProtocolVersion);
        Assert.AreEqual(string.Empty, status.SessionId);
        Assert.AreEqual(string.Empty, status.DeviceId);
        Assert.AreEqual(string.Empty, status.Discovery);
    }
}
