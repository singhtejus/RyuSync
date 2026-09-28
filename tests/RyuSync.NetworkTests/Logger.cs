namespace Ryujinx.Common.Logging;

// Only logging is substituted; all connection and input logic is production code.
internal enum LogClass { Hid }

internal sealed class TestLog
{
    public void Print(LogClass logClass, string message) => Console.WriteLine(message);
}

internal static class Logger
{
    public static TestLog Error { get; } = new();
    public static TestLog Warning { get; } = new();
    public static TestLog Info { get; } = new();
    public static TestLog Debug { get; } = new();
}
