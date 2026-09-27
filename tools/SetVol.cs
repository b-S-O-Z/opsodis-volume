// Test helper: SetVol.exe <0-100> [mute|unmute] sets the OPSODIS endpoint master volume.
using System;
using System.Globalization;

namespace OpsodisVolume
{
    internal static class SetVol
    {
        [MTAThread]
        static int Main(string[] args)
        {
            IMMDeviceEnumerator en = DeviceHelper.CreateEnumerator();
            IMMDeviceCollection col;
            en.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out col);
            int n; col.GetCount(out n);
            for (int i = 0; i < n; i++)
            {
                IMMDevice dev; col.Item(i, out dev);
                if (DeviceHelper.GetFriendlyName(dev).IndexOf("OPSODIS", StringComparison.OrdinalIgnoreCase) < 0) continue;
                IAudioEndpointVolume vol = DeviceHelper.Activate<IAudioEndpointVolume>(dev, AudioIids.IAudioEndpointVolume);
                Guid ctx = Guid.Empty;
                vol.SetMasterVolumeLevelScalar(float.Parse(args[0], CultureInfo.InvariantCulture) / 100f, ref ctx);
                if (args.Length > 1) vol.SetMute(args[1] == "mute", ref ctx);
                return 0;
            }
            return 1;
        }
    }
}
