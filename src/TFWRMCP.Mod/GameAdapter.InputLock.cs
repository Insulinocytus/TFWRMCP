using System;
using System.Threading.Tasks;

namespace TFWRMCP.Mod;

public sealed partial class GameAdapter
{
    readonly InputLock inputLock;

    public event Action UserTookOver
    {
        add => inputLock.TookOver += value;
        remove => inputLock.TookOver -= value;
    }

    public Task SetInputLock(string? tool) => MainThread.Run(() => inputLock.Set(tool));
}
