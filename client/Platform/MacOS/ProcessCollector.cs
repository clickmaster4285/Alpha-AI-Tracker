using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Platform.MacOS;

public class ProcessCollector : IActivityCollector
{
    private readonly string _machineId;

    public ProcessCollector(string machineId)
    {
        _machineId = machineId;
    }

    public Task<IReadOnlyList<ActivityLog>> CollectAsync(CancellationToken ct)
    {
        var logs = new List<ActivityLog>();
        var now = DateTime.UtcNow;
        var currentUser = Environment.UserName;

        var processes = Process.GetProcesses();

        foreach (var proc in processes)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var name = proc.ProcessName;
                var pid = proc.Id;
                if (pid == 0 || string.IsNullOrWhiteSpace(name)) continue;

                long mem = 0;
                try { mem = proc.WorkingSet64; } catch { }

                logs.Add(new ActivityLog
                {
                    MachineId = _machineId,
                    Timestamp = now,
                    ProcessName = name,
                    WindowTitle = null,
                    ProcessId = pid,
                    CpuPercent = 0,
                    MemoryBytes = mem,
                    IsForeground = false,
                    UserName = currentUser,
                    Platform = "macOS"
                });
            }
            catch
            {
                // process exited
            }
        }

        return Task.FromResult<IReadOnlyList<ActivityLog>>(logs);
    }
}
