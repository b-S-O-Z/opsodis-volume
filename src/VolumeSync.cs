// Mirrors the (ignored) hardware master volume of the target endpoint onto every audio
// session of that endpoint. Session volume is applied in software by the Windows audio
// engine, so it takes effect even though the USB device ignores its Feature Unit volume.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpsodisVolume
{
    internal sealed class VolumeSync : IDisposable
    {
        // Passed as event context on every change we make, so our own changes can be told
        // apart from changes made by the user (Volume Mixer) or by the application itself.
        internal static readonly Guid OurContext = new Guid("5b0e3c1e-9d7a-4f7e-8c43-0a1f7f2b6d11");

        const int HwSupportVolume = 0x1;
        const int SessionStateExpired = 2;

        readonly string keyword;
        readonly Thread thread;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Dictionary<string, TrackedSession> sessions = new Dictionary<string, TrackedSession>();
        internal readonly object Sync = new object();

        volatile bool stopping;
        volatile bool rebindRequested = true;
        volatile bool enabled;
        volatile string status = "起動中...";

        // Guarded by Sync: the gain most recently applied, used to derive per-app factors.
        internal double CurrentGain = 1.0;

        IMMDeviceEnumerator enumerator;
        DeviceNotifier deviceNotifier;
        IMMDevice device;
        string deviceName;
        IAudioEndpointVolume endpoint;
        EndpointCallback endpointCallback;
        IAudioSessionManager2 manager;
        SessionCreatedSink sessionCreatedSink;
        bool hwVolume;

        public VolumeSync(string deviceKeyword, bool startEnabled)
        {
            keyword = deviceKeyword;
            enabled = startEnabled;
            thread = new Thread(ThreadMain);
            thread.IsBackground = true;
            thread.Name = "VolumeSync";
            // Session notifications are only delivered to MTA threads.
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public string Status { get { return status; } }

        public bool Enabled
        {
            get { return enabled; }
            set { enabled = value; wake.Set(); }
        }

        internal void Wake() { wake.Set(); }
        internal void RequestRebind() { rebindRequested = true; wake.Set(); }

        public void Dispose()
        {
            if (stopping) return;
            stopping = true;
            wake.Set();
            thread.Join(3000);
        }

        void ThreadMain()
        {
            try
            {
                enumerator = DeviceHelper.CreateEnumerator();
                deviceNotifier = new DeviceNotifier(this);
                enumerator.RegisterEndpointNotificationCallback(deviceNotifier);
            }
            catch (Exception ex)
            {
                status = "初期化失敗: " + ex.Message;
                return;
            }

            while (!stopping)
            {
                try
                {
                    if (rebindRequested)
                    {
                        rebindRequested = false;
                        Rebind();
                    }
                    SyncOnce(enabled);
                }
                catch (Exception ex)
                {
                    // Typically AUDCLNT_E_DEVICE_INVALIDATED after unplugging; bind again.
                    status = "再接続待ち: " + ex.Message;
                    Unbind();
                    rebindRequested = true;
                }
                // The periodic pass is a safety net; volume changes and new sessions wake us immediately.
                wake.WaitOne(1000);
            }

            // Leave applications at their own volume (no attenuation) when we quit.
            try { SyncOnce(false); } catch { }
            Unbind();
            try { enumerator.UnregisterEndpointNotificationCallback(deviceNotifier); } catch { }
        }

        void Rebind()
        {
            Unbind();

            IMMDeviceCollection col;
            if (enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out col) != 0) return;
            int count;
            col.GetCount(out count);
            for (int i = 0; i < count && device == null; i++)
            {
                IMMDevice d;
                if (col.Item(i, out d) != 0) continue;
                string name = DeviceHelper.GetFriendlyName(d);
                if (name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    device = d;
                    deviceName = name.Trim();
                }
                else
                {
                    Marshal.ReleaseComObject(d);
                }
            }
            Marshal.ReleaseComObject(col);
            if (device == null) return;

            endpoint = DeviceHelper.Activate<IAudioEndpointVolume>(device, AudioIids.IAudioEndpointVolume);
            int mask;
            endpoint.QueryHardwareSupport(out mask);
            // Without hardware volume Windows already attenuates in software; we must not double it.
            hwVolume = (mask & HwSupportVolume) != 0;
            endpointCallback = new EndpointCallback(this);
            endpoint.RegisterControlChangeNotify(endpointCallback);

            manager = DeviceHelper.Activate<IAudioSessionManager2>(device, AudioIids.IAudioSessionManager2);
            // GetSessionEnumerator must be called once before RegisterSessionNotification works.
            IAudioSessionEnumerator se;
            if (manager.GetSessionEnumerator(out se) == 0) Marshal.ReleaseComObject(se);
            sessionCreatedSink = new SessionCreatedSink(this);
            manager.RegisterSessionNotification(sessionCreatedSink);
        }

        void Unbind()
        {
            foreach (TrackedSession s in sessions.Values) s.Detach();
            sessions.Clear();
            if (manager != null)
            {
                try { manager.UnregisterSessionNotification(sessionCreatedSink); } catch { }
                SafeRelease(manager);
                manager = null;
            }
            if (endpoint != null)
            {
                try { endpoint.UnregisterControlChangeNotify(endpointCallback); } catch { }
                SafeRelease(endpoint);
                endpoint = null;
            }
            if (device != null)
            {
                SafeRelease(device);
                device = null;
            }
        }

        void SyncOnce(bool active)
        {
            if (endpoint == null)
            {
                status = "「" + keyword + "」が見つかりません";
                return;
            }

            float scalar, db;
            bool mute;
            Check(endpoint.GetMasterVolumeLevelScalar(out scalar));
            Check(endpoint.GetMasterVolumeLevel(out db));
            Check(endpoint.GetMute(out mute));

            double gain = 1.0;
            if (active && hwVolume)
                gain = (mute || scalar <= 0.0005f) ? 0.0 : Math.Min(1.0, Math.Pow(10.0, db / 20.0));
            lock (Sync) CurrentGain = gain;

            IAudioSessionEnumerator se;
            Check(manager.GetSessionEnumerator(out se));
            HashSet<string> seen = new HashSet<string>();
            try
            {
                int n;
                se.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IAudioSessionControl ctl;
                    if (se.GetSession(i, out ctl) != 0 || ctl == null) continue;
                    int state;
                    ctl.GetState(out state);
                    if (state == SessionStateExpired) continue;
                    string id;
                    if (((IAudioSessionControl2)ctl).GetSessionInstanceIdentifier(out id) != 0 || id == null) continue;
                    seen.Add(id);

                    TrackedSession ts;
                    if (!sessions.TryGetValue(id, out ts))
                    {
                        ts = new TrackedSession(this, ctl);
                        sessions.Add(id, ts);
                    }
                    ts.Apply(gain);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(se);
            }

            List<string> gone = new List<string>();
            foreach (KeyValuePair<string, TrackedSession> kv in sessions)
                if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (string id in gone)
            {
                sessions[id].Detach();
                sessions.Remove(id);
            }

            string text = deviceName + ": " + (int)Math.Round(scalar * 100) + "%";
            if (mute) text += " ミュート";
            if (!hwVolume) text += " (Windows側で制御)";
            else if (!active) text += " [無効]";
            status = text;
        }

        static void Check(int hr)
        {
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        }

        static void SafeRelease(object o)
        {
            try { Marshal.ReleaseComObject(o); } catch { }
        }
    }

    // One application's audio session on the target endpoint.
    [ComVisible(true)]
    internal sealed class TrackedSession : IAudioSessionEvents
    {
        readonly VolumeSync owner;
        readonly IAudioSessionControl control;
        readonly ISimpleAudioVolume volume;
        bool attached;

        // The app's own volume relative to the master (what the user set in the Volume Mixer).
        // Starts at 1 on purpose: Windows persists session volumes per app, so the initial value
        // is often a leftover of our own attenuation from a previous run.
        double factor = 1.0; // guarded by owner.Sync

        public TrackedSession(VolumeSync owner, IAudioSessionControl control)
        {
            this.owner = owner;
            this.control = control;
            volume = (ISimpleAudioVolume)control;
            attached = control.RegisterAudioSessionNotification(this) == 0;
        }

        public void Apply(double gain)
        {
            double f;
            lock (owner.Sync) f = factor;
            float target = (float)Math.Min(1.0, f * gain);
            float current;
            if (volume.GetMasterVolume(out current) != 0) return;
            if (Math.Abs(current - target) > 0.0005f)
            {
                Guid ctx = VolumeSync.OurContext;
                volume.SetMasterVolume(target, ref ctx);
            }
        }

        public void Detach()
        {
            if (!attached) return;
            attached = false;
            try { control.UnregisterAudioSessionNotification(this); } catch { }
        }

        public int OnSimpleVolumeChanged(float newVolume, bool newMute, ref Guid eventContext)
        {
            if (eventContext == VolumeSync.OurContext) return 0;
            lock (owner.Sync)
            {
                double g = owner.CurrentGain;
                if (g > 0.001) factor = Math.Min(1.0, newVolume / g);
            }
            // If the requested level exceeds the master, clamp it back on the worker thread.
            owner.Wake();
            return 0;
        }

        public int OnDisplayNameChanged(string newDisplayName, ref Guid eventContext) { return 0; }
        public int OnIconPathChanged(string newIconPath, ref Guid eventContext) { return 0; }
        public int OnChannelVolumeChanged(int channelCount, IntPtr newChannelVolumeArray, int changedChannel, ref Guid eventContext) { return 0; }
        public int OnGroupingParamChanged(ref Guid newGroupingParam, ref Guid eventContext) { return 0; }
        public int OnStateChanged(int newState) { owner.Wake(); return 0; }
        public int OnSessionDisconnected(int disconnectReason) { owner.Wake(); return 0; }
    }

    [ComVisible(true)]
    internal sealed class EndpointCallback : IAudioEndpointVolumeCallback
    {
        readonly VolumeSync owner;
        public EndpointCallback(VolumeSync owner) { this.owner = owner; }
        public int OnNotify(IntPtr notifyData) { owner.Wake(); return 0; }
    }

    [ComVisible(true)]
    internal sealed class SessionCreatedSink : IAudioSessionNotification
    {
        readonly VolumeSync owner;
        public SessionCreatedSink(VolumeSync owner) { this.owner = owner; }
        public int OnSessionCreated(IAudioSessionControl newSession) { owner.Wake(); return 0; }
    }

    [ComVisible(true)]
    internal sealed class DeviceNotifier : IMMNotificationClient
    {
        readonly VolumeSync owner;
        public DeviceNotifier(VolumeSync owner) { this.owner = owner; }
        public void OnDeviceStateChanged(string deviceId, int newState) { owner.RequestRebind(); }
        public void OnDeviceAdded(string deviceId) { owner.RequestRebind(); }
        public void OnDeviceRemoved(string deviceId) { owner.RequestRebind(); }
        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string defaultDeviceId) { }
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }
}
