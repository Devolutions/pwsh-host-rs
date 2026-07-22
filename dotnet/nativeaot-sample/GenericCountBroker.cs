using System.Runtime.InteropServices.Marshalling;
using Devolutions.PowerShell.Ffi;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace NativeAotFfiSample;

[GeneratedComClass]
internal sealed partial class GenericCountBroker : IPowerShellLiveObjectTestCount, IPowerShellLiveObjectBroker
{
    private const int EFail = unchecked((int)0x80004005);
    private readonly object gate = new();
    private long count;
    private bool disposed;

    internal GenericCountBroker(long initialCount)
    {
        count = initialCount;
    }

    public int GetCount(out long value)
    {
        lock (gate)
        {
            if (disposed)
            {
                value = default;
                return EFail;
            }

            value = count;
            return 0;
        }
    }

    public int Increment(out long value)
    {
        lock (gate)
        {
            if (disposed || count == long.MaxValue)
            {
                value = default;
                return EFail;
            }

            value = ++count;
            return 0;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
        }
    }
}
