using System;
using System.Runtime.InteropServices.Marshalling;
using Devolutions.PowerShell.Ffi;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace NativeAotFfiSample;

[GeneratedComClass]
internal sealed partial class GenericCountBroker : IPowerShellLiveObjectTestCount, IPowerShellLiveObjectBroker
{
    private const int EFail = unchecked((int)0x80004005);
    private readonly object gate = new();
    private readonly GenericChildBroker primary = new(11);
    private readonly GenericChildBroker secondary = new(22);
    private readonly GenericChildCollectionBroker children;
    private long count;
    private long revision;
    private bool disposed;

    internal GenericCountBroker(long initialCount)
    {
        count = initialCount;
        children = new GenericChildCollectionBroker([primary, secondary]);
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

    public int GetRevision(out long value)
    {
        lock (gate)
        {
            if (disposed)
            {
                value = default;
                return EFail;
            }

            value = revision;
            return 0;
        }
    }

    public int SetRevision(long value)
    {
        lock (gate)
        {
            if (disposed)
            {
                return EFail;
            }

            revision = value;
            return 0;
        }
    }

    public int GetPrimary(out IPowerShellLiveObjectTestChild value)
    {
        lock (gate)
        {
            if (disposed)
            {
                value = null!;
                return EFail;
            }

            value = primary;
            return 0;
        }
    }

    public int GetChildren(out IPowerShellLiveObjectTestChildCollection value)
    {
        lock (gate)
        {
            if (disposed)
            {
                value = null!;
                return EFail;
            }

            value = children;
            return 0;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            primary.Dispose();
            secondary.Dispose();
            children.Dispose();
        }
    }
}

[GeneratedComClass]
internal sealed partial class GenericChildBroker : IPowerShellLiveObjectTestChild, IDisposable
{
    private const int EFail = unchecked((int)0x80004005);
    private readonly object gate = new();
    private long value;
    private bool disposed;

    internal GenericChildBroker(long value)
    {
        this.value = value;
    }

    public int GetValue(out long result)
    {
        lock (gate)
        {
            if (disposed)
            {
                result = default;
                return EFail;
            }

            result = value;
            return 0;
        }
    }

    public int SetValue(long value)
    {
        lock (gate)
        {
            if (disposed)
            {
                return EFail;
            }

            this.value = value;
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

[GeneratedComClass]
internal sealed partial class GenericChildCollectionBroker : IPowerShellLiveObjectTestChildCollection, IDisposable
{
    private const int EFail = unchecked((int)0x80004005);
    private readonly object gate = new();
    private readonly GenericChildBroker[] values;
    private bool disposed;

    internal GenericChildCollectionBroker(GenericChildBroker[] values)
    {
        this.values = values;
    }

    public int GetCount(out int count)
    {
        lock (gate)
        {
            if (disposed)
            {
                count = default;
                return EFail;
            }

            count = values.Length;
            return 0;
        }
    }

    public int GetAt(int index, out IPowerShellLiveObjectTestChild value)
    {
        lock (gate)
        {
            if (disposed || (uint)index >= (uint)values.Length)
            {
                value = null!;
                return EFail;
            }

            value = values[index];
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
