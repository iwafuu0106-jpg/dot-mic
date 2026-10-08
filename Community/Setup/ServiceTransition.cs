using System.ComponentModel;

namespace DotMic.Setup;

internal static class ServiceTransition
{
    internal sealed record Snapshot(uint State, uint Checkpoint, uint WaitHint, uint Error = 0);
    internal enum Command { Start, Stop, Pause, Continue }
    internal static uint ControlAccess(Command command) => command switch {
        Command.Start => 0x14, Command.Stop => 0x24, _ => 0x44
    };
    internal static void Run(Func<Snapshot> query, Action<Command> command, Func<long> clock, Action<int> sleep,
        string name, bool start, bool paused = false)
    {
        long began = clock(), progress = began;
        uint lastState = 0, lastCheckpoint = 0;
        uint? commandedState = null;
        uint wanted = !start ? 1u : paused ? 7u : 4u;
        while (true) {
            var current = query(); long now = clock();
            if (current.State == wanted) return;
            if (current.State is < 1 or > 7) throw new IOException("サービスの状態が不正です: " + name);
            if (current.State != lastState || current.Checkpoint > lastCheckpoint) {
                progress = now; lastState = current.State; lastCheckpoint = current.Checkpoint;
                if (commandedState != current.State) commandedState = null;
            }
            long stallBudget = Math.Clamp((long)current.WaitHint, 20000, 60000);
            if (now - began >= 180000 || now - progress >= stallBudget)
                throw new IOException("サービスの状態遷移が完了しません: " + name + " (状態 " + current.State + ", error " + current.Error + ")。PC再起動が必要な場合は利用者が手動で行ってください。");
            bool pending = current.State is 2 or 3 or 5 or 6;
            if (!pending && commandedState != current.State) {
                Command action = !start ? Command.Stop : current.State == 1 ? Command.Start : current.State == 7 ? Command.Continue : Command.Pause;
                try { command(action); commandedState = current.State; }
                catch (Win32Exception error) when (error.NativeErrorCode == 1061
                    || error.NativeErrorCode == 1056 && action == Command.Start
                    || error.NativeErrorCode == 1062 && action == Command.Stop) {
                    // Another controller may have changed state after Query. Requery
                    // within the same deadline; never ignore a permanent failure.
                }
            }
            sleep(100);
        }
    }
}
