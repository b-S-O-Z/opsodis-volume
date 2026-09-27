// Diagnostic: prints volume capabilities of all active render endpoints and their sessions.
using System;
using System.Runtime.InteropServices;

namespace OpsodisVolume
{
    internal static class Probe
    {
        [MTAThread]
        static void Main()
        {
            IMMDeviceEnumerator en = DeviceHelper.CreateEnumerator();
            IMMDeviceCollection col;
            en.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out col);
            int n; col.GetCount(out n);
            for (int i = 0; i < n; i++)
            {
                IMMDevice dev; col.Item(i, out dev);
                Console.WriteLine("== " + DeviceHelper.GetFriendlyName(dev) + "  " + DeviceHelper.GetId(dev));
                IAudioEndpointVolume vol = DeviceHelper.Activate<IAudioEndpointVolume>(dev, AudioIids.IAudioEndpointVolume);
                int hw; vol.QueryHardwareSupport(out hw);
                float mn, mx, inc; vol.GetVolumeRange(out mn, out mx, out inc);
                float db, sc; vol.GetMasterVolumeLevel(out db); vol.GetMasterVolumeLevelScalar(out sc);
                int step, steps; vol.GetVolumeStepInfo(out step, out steps);
                bool mute; vol.GetMute(out mute);
                Console.WriteLine(string.Format("   hwMask=0x{0:X} range={1}..{2} dB inc={3} steps={4} now={5:F3} ({6:F2} dB) mute={7}",
                    hw, mn, mx, inc, steps, sc, db, mute));
                IAudioSessionManager2 mgr = DeviceHelper.Activate<IAudioSessionManager2>(dev, AudioIids.IAudioSessionManager2);
                IAudioSessionEnumerator se; mgr.GetSessionEnumerator(out se);
                int sn; se.GetCount(out sn);
                for (int j = 0; j < sn; j++)
                {
                    IAudioSessionControl sc1; se.GetSession(j, out sc1);
                    IAudioSessionControl2 sc2 = (IAudioSessionControl2)sc1;
                    int pid; sc2.GetProcessId(out pid);
                    float sv; ((ISimpleAudioVolume)sc1).GetMasterVolume(out sv);
                    int st; sc1.GetState(out st);
                    Console.WriteLine(string.Format("   session pid={0} state={1} vol={2:F3}", pid, st, sv));
                }
            }
        }
    }
}
