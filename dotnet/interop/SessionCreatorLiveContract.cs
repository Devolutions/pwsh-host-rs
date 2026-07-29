#nullable enable

using System.Collections.Generic;
using Devolutions.PowerShell.Ffi.LiveObjects;

namespace Devolutions.MultiPwsh.LiveContracts;

[LiveContract("A1E95B5C-9E8E-4A3D-B6F2-93F81C51B19E", 1, 0, "5BFBF6D7-7BFA-4C15-8D35-02B665F39A18")]
[LiveObject(1)]
public interface ISessionCreatorLiveContract
{
    [LiveMember(1, ResultObjectId = 3, MaximumUtf8Bytes = 128)]
    ISessionCreatorLiveChild Add(string name);

    [LiveMember(2, ResultObjectId = 2, MaximumCollectionCount = 32)]
    IReadOnlyList<ISessionCreatorLiveChild> Children { get; }
}

[LiveObject(2)]
public interface ISessionCreatorLiveChildren
{
    [LiveMember(3, MaximumCollectionCount = 32)]
    int Count { get; }

    [LiveMember(4, ResultObjectId = 3)]
    ISessionCreatorLiveChild GetAt(int index);
}

[LiveObject(3)]
public interface ISessionCreatorLiveChild
{
    [LiveMember(10, SetterId = 11, MaximumUtf8Bytes = 128)]
    string Name { get; set; }

    [LiveMember(12, SetterId = 13, MaximumUtf8Bytes = 128)]
    string Host { get; set; }

    [LiveMember(14, SetterId = 15, MaximumUtf8Bytes = 128)]
    string Description { get; set; }

    [LiveMember(16, SetterId = 17, MaximumUtf8Bytes = 128)]
    string Group { get; set; }
}
