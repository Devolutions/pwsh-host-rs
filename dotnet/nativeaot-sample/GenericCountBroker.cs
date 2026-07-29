using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.Marshalling;
using Devolutions.PowerShell.Ffi;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace NativeAotFfiSample;

[GeneratedComClass]
internal sealed partial class GenericCountBroker :
    IPowerShellLiveObjectTestCount,
    IPowerShellLiveObjectTestChildCollection,
    IPowerShellLiveObjectBroker
{
    private const int EFail = unchecked((int)0x80004005);
    private readonly object gate = new();
    private readonly GenericChildBroker primary = new(1, 11, "primary");
    private readonly List<GenericChildBroker> children;
    private long count;
    private long nextChildIdentity = 3;
    private bool disposed;

    internal GenericCountBroker(long initialCount)
    {
        count = initialCount;
        children =
        [
            primary,
            new GenericChildBroker(2, 22, "secondary"),
        ];
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

            value = this;
            return 0;
        }
    }

    public int Add(string name, out IPowerShellLiveObjectTestChild value)
    {
        lock (gate)
        {
            if (disposed || !GenericChildBroker.IsValidText(name))
            {
                value = null!;
                return EFail;
            }

            var child = new GenericChildBroker(nextChildIdentity++, 0, name);
            children.Add(child);
            value = child;
            return 0;
        }
    }

    public int GetCount(out int value)
    {
        lock (gate)
        {
            if (disposed)
            {
                value = default;
                return EFail;
            }

            value = children.Count;
            return 0;
        }
    }

    public int GetAt(int index, out IPowerShellLiveObjectTestChild value)
    {
        lock (gate)
        {
            if (disposed || (uint)index >= (uint)children.Count)
            {
                value = null!;
                return EFail;
            }

            value = children[index];
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
            foreach (GenericChildBroker child in children)
            {
                child.Dispose();
            }
        }
    }
}

[GeneratedComClass]
internal sealed partial class GenericChildBroker : IPowerShellLiveObjectTestChild, IDisposable
{
    private const int EFail = unchecked((int)0x80004005);
    private readonly object gate = new();
    private readonly long identity;
    private long value;
    private string name;
    private string host = string.Empty;
    private string description = string.Empty;
    private string group = string.Empty;
    private bool disposed;

    internal GenericChildBroker(long identity, long value, string name)
    {
        this.identity = identity;
        this.value = value;
        this.name = name;
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

    public int GetIdentity(out long value)
    {
        lock (gate)
        {
            if (disposed)
            {
                value = default;
                return EFail;
            }

            value = identity;
            return 0;
        }
    }

    public int GetName(out string value)
    {
        return GetText(static child => child.name, out value);
    }

    public int SetName(string value)
    {
        return SetText(value, static (child, text) => child.name = text);
    }

    public int GetHost(out string value)
    {
        return GetText(static child => child.host, out value);
    }

    public int SetHost(string value)
    {
        return SetText(value, static (child, text) => child.host = text);
    }

    public int GetDescription(out string value)
    {
        return GetText(static child => child.description, out value);
    }

    public int SetDescription(string value)
    {
        return SetText(value, static (child, text) => child.description = text);
    }

    public int GetGroup(out string value)
    {
        return GetText(static child => child.group, out value);
    }

    public int SetGroup(string value)
    {
        return SetText(value, static (child, text) => child.group = text);
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
        }
    }

    internal static bool IsValidText(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
    }

    private int GetText(Func<GenericChildBroker, string> getter, out string result)
    {
        lock (gate)
        {
            if (disposed)
            {
                result = string.Empty;
                return EFail;
            }

            result = getter(this);
            return 0;
        }
    }

    private int SetText(string value, Action<GenericChildBroker, string> setter)
    {
        lock (gate)
        {
            if (disposed || !IsValidText(value))
            {
                return EFail;
            }

            setter(this, value);
            return 0;
        }
    }
}
