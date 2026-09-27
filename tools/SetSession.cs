// Test helper: SetSession.exe <pid> <0.0-1.0> sets one session's volume like the Volume Mixer would.
using System;
using System.Globalization;

namespace OpsodisVolume
{
    internal static class SetSession
    {
        [MTAThread]
        static int Main(string[] args)
        {
            int pid = int.Parse(args[0]);
            float v = float.Parse(args[1], CultureInfo.InvariantCulture);
            IMMDeviceEnumerator en = DeviceHelper.CreateEnumerator();
            IMMDeviceCollection col;
            en.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out col);
            int n; col.GetCount(out n);
            for (int i = 0; i < n; i++)
            {
                IMMDevice dev; col.Item(i, out dev);
                if (DeviceHelper.GetFriendlyName(dev).IndexOf("OPSODIS", StringComparison.OrdinalIgnoreCase) < 0) continue;
                IAudioSessionManager2 mgr = DeviceHelper.Activate<IAudioSessionManager2>(dev, AudioIids.IAudioSessionManager2);
                IAudioSessionEnumerator se; mgr.GetSessionEnumerator(out se);
                int sn; se.GetCount(out sn);
                for (int j = 0; j < sn; j++)
                {
                    IAudioSessionControl c; se.GetSession(j, out c);
                    int p; ((IAudioSessionControl2)c).GetProcessId(out p);
                    if (p != pid) continue;
                    Guid ctx = Guid.NewGuid();
                    ((ISimpleAudioVolume)c).SetMasterVolume(v, ref ctx);
                    return 0;
                }
            }
            return 1;
        }
    }
}
