using System.Text;

namespace MeshDrive.Protocol;

internal static class PipeUtf8
{
    public static StreamReader CreateReader(Stream stream) =>
        new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

    public static StreamWriter CreateWriter(Stream stream) =>
        new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true)
        {
            AutoFlush = true,
        };
}
