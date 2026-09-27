// Diagnostic: maps endpoint scalar -> dB on the OPSODIS endpoint, then restores the original level.
using System;

namespace OpsodisVolume
{
    internal static class Curve
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
                if (DeviceHelper.GetFriendlyName(dev).IndexOf("OPSODIS", StringComparison.OrdinalIgnoreCase) < 0) continue;
                IAudioEndpointVolume vol = DeviceHelper.Activate<IAudioEndpointVolume>(dev, AudioIids.IAudioEndpointVolume);
                Guid ctx = Guid.Empty;
                float orig; vol.GetMasterVolumeLevelScalar(out orig);
                foreach (float s in new float[] { 0f, 0.01f, 0.02f, 0.05f, 0.1f, 0.2f, 0.3f, 0.5f, 0.7f, 0.9f, 1f })
                {
                    vol.SetMasterVolumeLevelScalar(s, ref ctx);
                    float db, back; vol.GetMasterVolumeLevel(out db); vol.GetMasterVolumeLevelScalar(out back);
                    Console.WriteLine(string.Format("{0:F2} -> {1:F2} dB (reads {2:F3})", s, db, back));
                }
                vol.SetMasterVolumeLevelScalar(orig, ref ctx);
            }
        }
    }
}
