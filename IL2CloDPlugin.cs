using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Threading;
using YawGLAPI;

#nullable disable

namespace IL2CloDPlugin
{
    [Export(typeof(Game))]
    [ExportMetadata("Name", "IL-2 Cliffs of Dover")]
    [ExportMetadata("Version", "0.5")]
    internal class IL2CloDPlugin : Game
    {
        private const int STEAM_APP_ID = 754530;

        private static readonly string[] MapNames = new[]
        {
            "CLODDeviceLink",
            @"Global\CLODDeviceLink",
            @"Local\CLODDeviceLink"
        };

        private const int MaxSlots = 10000;
        private const int PollIntervalMs = 25;
        private const int StreamTimeoutMs = 3000;
        private const int HomeSteps = 50;
        private const int HomeStepMs = 40;

        // Pause / resume behaviour
        private const int PauseDetectFrames = 8;        // ~200 ms at 25 ms poll
        private const int ResumeBlendFrames = 6;        // ~150 ms
        private const float PauseDynamicDecay = 0.75f; // per frame while paused

        // Frozen-orientation detection thresholds
        private const float YawFreezeEpsDeg = 0.02f;
        private const float PitchFreezeEpsDeg = 0.02f;
        private const float RollFreezeEpsDeg = 0.02f;

        // Soft limits for pose channels
        private const float SoftZone = 0.65f;
        private const float YawLimitDeg = 90f;
        private const float PitchFwdLimitDeg = 15f;
        private const float PitchBackLimitDeg = 45f;
        private const float RollLimitDeg = 15f;

        private const float YawProcessedSign = -1f;
        private const float PitchSign = 1f;
        private const float RollSign = 1f;

        // Confirmed live orientation slots
        private const int SLOT_YAW_SIGNED = 840;
        private const int SLOT_PITCH = 841;
        private const int SLOT_ROLL = 842;

        // Legacy / instrument-style channels identified from old DeviceLink docs + live reader behaviour
        // Notes:
        // - 1480 lines up very well with I_EngineRPM
        // - 1609 / 1619 / 1629 / 1639 / 1749 line up with subtype -1 reads for I_VelocityIAS / I_Altitude / I_Variometer / I_Slip / I_Turn
        // - 1650/1651/1652 appear to be magnetic compass related
        // - 1769 appears to behave like a heading / direction-indicator style value and is still useful as Heading360
        private const int SLOT_I_ENGINERPM_1480 = 1480;
        private const int SLOT_I_VELOCITYIAS_M1_1609 = 1609;
        private const int SLOT_I_ALTITUDE_M1_1619 = 1619;
        private const int SLOT_I_VARIOMETER_M1_1629 = 1629;
        private const int SLOT_I_SLIP_M1_1639 = 1639;
        private const int SLOT_I_MAGCOMP_1650 = 1650;
        private const int SLOT_I_MAGCOMP_1651 = 1651;
        private const int SLOT_I_MAGCOMP_1652 = 1652;
        private const int SLOT_I_TURN_M1_1749 = 1749;
        private const int SLOT_I_AH_1761 = 1761;
        private const int SLOT_I_AH_1762 = 1762;

        // Still-useful extras / uncertain but previously discovered
        private const int SLOT_HEADING_360 = 1769; // likely direction-indicator / heading-like channel
        private const int SLOT_1490 = 1490;
        private const int SLOT_1010 = 1010;

        // Output channels
        private const int IDX_YAW_SIGNED = 0;
        private const int IDX_PITCH_RAW = 1;
        private const int IDX_ROLL_RAW = 2;
        private const int IDX_HEADING_360 = 3;
        private const int IDX_YAW_DELTA = 4;
        private const int IDX_YAW_PROCESSED = 5;
        private const int IDX_PITCH_SOFT = 6;
        private const int IDX_ROLL_SOFT = 7;

        private const int IDX_I_ENGINERPM_1480 = 8;
        private const int IDX_I_VELOCITYIAS_M1_1609 = 9;
        private const int IDX_I_ALTITUDE_M1_1619 = 10;
        private const int IDX_I_VARIOMETER_M1_1629 = 11;
        private const int IDX_I_SLIP_M1_1639 = 12;
        private const int IDX_I_MAGCOMP_1650 = 13;
        private const int IDX_I_MAGCOMP_1651 = 14;
        private const int IDX_I_MAGCOMP_1652 = 15;
        private const int IDX_I_TURN_M1_1749 = 16;
        private const int IDX_I_AH_1761 = 17;
        private const int IDX_I_AH_1762 = 18;

        private const int IDX_SLOT_1490 = 19;
        private const int IDX_SLOT_1010 = 20;

        private const int CHANNEL_COUNT = 21;

        // Dynamic channels that should gently settle while paused.
        private static readonly int[] DynamicChannelIndices = new int[]
        {
            IDX_YAW_DELTA,
            IDX_I_VELOCITYIAS_M1_1609,
            IDX_I_VARIOMETER_M1_1629,
            IDX_I_SLIP_M1_1639,
            IDX_I_TURN_M1_1749
        };

        private Thread readThread;
        private bool stop;
        private IMainFormDispatcher dispatcher;
        private IProfileManager controller;

        private MemoryMappedFile mmf;
        private MemoryMappedViewAccessor accessor;

        private readonly object mmfLock = new object();
        private readonly object homeLock = new object();

        private readonly float[] disp = new float[CHANNEL_COUNT];
        private readonly float[] target = new float[CHANNEL_COUNT];
        private readonly float[] resumeFrom = new float[CHANNEL_COUNT];

        private bool hadHeading;
        private float lastHeading360;
        private float yawAccumDeg;
        private DateTime lastValidPacketTime = DateTime.MinValue;
        private bool returnedHome;

        // Pause detection state
        private bool hadOrientationSample;
        private float lastSampleHeading360;
        private float lastSamplePitch;
        private float lastSampleRoll;
        private int frozenFrameCount;
        private bool wasPaused;
        private int resumeBlendFramesRemaining;

        public int STEAM_ID => STEAM_APP_ID;
        public string AUTHOR => "YawVR / community";
        public string PROCESS_NAME => string.Empty;
        public bool PATCH_AVAILABLE => false;
        public string Description => "IL-2 Cliffs of Dover shared-memory GameLink plugin using CLODDeviceLink.";

        public Stream Logo => GetStream("logo.png");
        public Stream SmallLogo => GetStream("recent.png");
        public Stream Background => GetStream("wide.png");

        public LedEffect DefaultLED()
        {
            return new LedEffect((EFFECT_TYPE)3, 2, new YawColor[4]
            {
                new YawColor(66, 135, 245),
                new YawColor(80, 80, 80),
                new YawColor(128, 3, 117),
                new YawColor(110, 201, 12)
            }, 0.7f);
        }

        public List<Profile_Component> DefaultProfile()
        {
            return new List<Profile_Component>()
            {
                new Profile_Component(0, IDX_YAW_PROCESSED, 1f, 1f, 0f, false, false, -1f, 1f, true,
                    (ObservableCollection<ProfileMath>)null, (ProfileSpikeflatter)null, 0f, (ProfileComponentType)0,
                    (ObservableCollection<ProfileCondition>)null),

                new Profile_Component(1, IDX_PITCH_SOFT, 1f, 1f, 0f, false, false, -1f, 1f, true,
                    (ObservableCollection<ProfileMath>)null, (ProfileSpikeflatter)null, 0f, (ProfileComponentType)0,
                    (ObservableCollection<ProfileCondition>)null),

                new Profile_Component(2, IDX_ROLL_SOFT, 1f, 1f, 0f, false, true, -1f, 1f, true,
                    (ObservableCollection<ProfileMath>)null, (ProfileSpikeflatter)null, 0f, (ProfileComponentType)0,
                    (ObservableCollection<ProfileCondition>)null),
            };
        }

        public string[] GetInputData()
        {
            return new string[CHANNEL_COUNT]
            {
                "Yaw_Signed_840",
                "Pitch_841",
                "Roll_842",
                "Heading360_1769",
                "Yaw_Delta",
                "Yaw_Processed",
                "Pitch_Soft",
                "Roll_Soft",

                "I_EngineRPM_1480",
                "I_VelocityIAS_m1_1609",
                "I_Altitude_m1_1619",
                "I_Variometer_m1_1629",
                "I_Slip_m1_1639",
                "I_MagneticCompass_1650",
                "I_MagneticCompass_1651",
                "I_MagneticCompass_1652",
                "I_Turn_m1_1749",
                "I_AH_1761",
                "I_AH_1762",

                "Slot_1490",
                "Slot_1010"
            };
        }

        public void SetReferences(IProfileManager controller, IMainFormDispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
            this.controller = controller;
        }

        public void Init()
        {
            Console.WriteLine("IL2 CLOD INIT v0.5");
            stop = false;
            returnedHome = false;

            hadHeading = false;
            yawAccumDeg = 0f;
            lastHeading360 = 0f;

            hadOrientationSample = false;
            lastSampleHeading360 = 0f;
            lastSamplePitch = 0f;
            lastSampleRoll = 0f;
            frozenFrameCount = 0;
            wasPaused = false;
            resumeBlendFramesRemaining = 0;

            lastValidPacketTime = DateTime.MinValue;

            Array.Clear(disp, 0, CHANNEL_COUNT);
            Array.Clear(target, 0, CHANNEL_COUNT);
            Array.Clear(resumeFrom, 0, CHANNEL_COUNT);

            readThread = new Thread(new ThreadStart(ReadFunction));
            readThread.IsBackground = true;
            readThread.Start();
        }

        public void Exit()
        {
            stop = true;

            try
            {
                readThread?.Join(500);
            }
            catch
            {
            }

            DisconnectMMF();
            ReturnToHome();
        }

        private void ReadFunction()
        {
            while (!stop)
            {
                try
                {
                    if (accessor == null && !TryConnectMMF())
                    {
                        MaybeReturnHome();
                        Thread.Sleep(100);
                        continue;
                    }

                    if (TryReadFrame(out float heading360, out float pitch, out float roll))
                    {
                        lastValidPacketTime = DateTime.UtcNow;
                        returnedHome = false;

                        bool isPaused = DetectPaused(heading360, pitch, roll);

                        if (isPaused)
                        {
                            HandlePausedFrame();
                        }
                        else
                        {
                            if (wasPaused)
                            {
                                Array.Copy(disp, resumeFrom, CHANNEL_COUNT);
                                resumeBlendFramesRemaining = ResumeBlendFrames;
                                wasPaused = false;
                            }

                            if (resumeBlendFramesRemaining > 0)
                            {
                                int blendStep = ResumeBlendFrames - resumeBlendFramesRemaining + 1;
                                float t = blendStep / (float)ResumeBlendFrames;
                                t = SmoothStep(t);

                                for (int i = 0; i < CHANNEL_COUNT; i++)
                                    disp[i] = Lerp(resumeFrom[i], target[i], t);

                                resumeBlendFramesRemaining--;
                            }
                            else
                            {
                                Array.Copy(target, disp, CHANNEL_COUNT);
                            }
                        }

                        PushDisplayedInputs();
                    }
                    else
                    {
                        MaybeReturnHome();
                    }

                    Thread.Sleep(PollIntervalMs);
                }
                catch (ObjectDisposedException)
                {
                    DisconnectMMF();
                }
                catch (IOException)
                {
                    DisconnectMMF();
                }
                catch
                {
                    DisconnectMMF();
                    MaybeReturnHome();
                    Thread.Sleep(100);
                }
            }
        }

        private bool TryConnectMMF()
        {
            lock (mmfLock)
            {
                if (accessor != null)
                    return true;

                foreach (string name in MapNames)
                {
                    try
                    {
                        mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                        accessor = mmf.CreateViewAccessor(0, MaxSlots * sizeof(double), MemoryMappedFileAccess.Read);
                        return true;
                    }
                    catch
                    {
                        DisconnectMMF_NoLock();
                    }
                }
            }

            return false;
        }

        private void DisconnectMMF()
        {
            lock (mmfLock)
            {
                DisconnectMMF_NoLock();
            }
        }

        private void DisconnectMMF_NoLock()
        {
            try { accessor?.Dispose(); } catch { }
            try { mmf?.Dispose(); } catch { }

            accessor = null;
            mmf = null;
        }

        private bool TryReadFrame(out float heading360, out float pitch, out float roll)
        {
            heading360 = 0f;
            pitch = 0f;
            roll = 0f;

            double yawSignedD = ReadValidatedSlot(SLOT_YAW_SIGNED, -360.0, 360.0);
            double pitchD = ReadValidatedSlot(SLOT_PITCH, -360.0, 360.0);
            double rollD = ReadValidatedSlot(SLOT_ROLL, -360.0, 360.0);

            if (double.IsNaN(yawSignedD) || double.IsNaN(pitchD) || double.IsNaN(rollD))
                return false;

            float yawSigned = (float)yawSignedD;
            pitch = (float)pitchD * PitchSign;
            roll = (float)rollD * RollSign;

            double heading360D = ReadValidatedSlot(SLOT_HEADING_360, -720.0, 720.0);
            heading360 = !double.IsNaN(heading360D) ? Wrap360((float)heading360D) : Wrap360(yawSigned);

            float yawDelta = 0f;
            if (!hadHeading)
            {
                hadHeading = true;
                lastHeading360 = heading360;
                yawAccumDeg = 0f;
            }
            else
            {
                yawDelta = NormalizeAngleDeltaDeg(heading360 - lastHeading360);
                lastHeading360 = heading360;
                yawAccumDeg += yawDelta;
            }

            target[IDX_YAW_SIGNED] = yawSigned;
            target[IDX_PITCH_RAW] = pitch;
            target[IDX_ROLL_RAW] = roll;
            target[IDX_HEADING_360] = heading360;
            target[IDX_YAW_DELTA] = yawDelta;
            target[IDX_YAW_PROCESSED] = SoftLimit(yawAccumDeg * YawProcessedSign, YawLimitDeg, SoftZone);
            target[IDX_PITCH_SOFT] = SoftLimitAsymmetric(pitch, PitchFwdLimitDeg, PitchBackLimitDeg, SoftZone);
            target[IDX_ROLL_SOFT] = SoftLimit(roll, RollLimitDeg, SoftZone);

            // Newly identified / relabeled legacy-style telemetry
            target[IDX_I_ENGINERPM_1480] = ReadSlotOrZero(SLOT_I_ENGINERPM_1480, 0.0, 10000.0);
            target[IDX_I_VELOCITYIAS_M1_1609] = ReadSlotOrZero(SLOT_I_VELOCITYIAS_M1_1609, -100000.0, 100000.0);
            target[IDX_I_ALTITUDE_M1_1619] = ReadSlotOrZero(SLOT_I_ALTITUDE_M1_1619, -1000000.0, 1000000.0);
            target[IDX_I_VARIOMETER_M1_1629] = ReadSlotOrZero(SLOT_I_VARIOMETER_M1_1629, -100000.0, 100000.0);
            target[IDX_I_SLIP_M1_1639] = ReadSlotOrZero(SLOT_I_SLIP_M1_1639, -1000.0, 1000.0);
            target[IDX_I_MAGCOMP_1650] = ReadSlotOrZero(SLOT_I_MAGCOMP_1650, -3600.0, 3600.0);
            target[IDX_I_MAGCOMP_1651] = ReadSlotOrZero(SLOT_I_MAGCOMP_1651, -3600.0, 3600.0);
            target[IDX_I_MAGCOMP_1652] = ReadSlotOrZero(SLOT_I_MAGCOMP_1652, -3600.0, 3600.0);
            target[IDX_I_TURN_M1_1749] = ReadSlotOrZero(SLOT_I_TURN_M1_1749, -1000.0, 1000.0);
            target[IDX_I_AH_1761] = ReadSlotOrZero(SLOT_I_AH_1761, -360.0, 360.0);
            target[IDX_I_AH_1762] = ReadSlotOrZero(SLOT_I_AH_1762, -360.0, 360.0);

            // Keep a couple of previously discovered extras around
            target[IDX_SLOT_1490] = ReadSlotOrZero(SLOT_1490, -100000.0, 100000.0);
            target[IDX_SLOT_1010] = ReadSlotOrZero(SLOT_1010, -100000.0, 100000.0);

            return true;
        }

        private bool DetectPaused(float heading360, float pitch, float roll)
        {
            if (!hadOrientationSample)
            {
                hadOrientationSample = true;
                lastSampleHeading360 = heading360;
                lastSamplePitch = pitch;
                lastSampleRoll = roll;
                frozenFrameCount = 0;
                return false;
            }

            float yawDelta = Math.Abs(NormalizeAngleDeltaDeg(heading360 - lastSampleHeading360));
            float pitchDelta = Math.Abs(pitch - lastSamplePitch);
            float rollDelta = Math.Abs(roll - lastSampleRoll);

            lastSampleHeading360 = heading360;
            lastSamplePitch = pitch;
            lastSampleRoll = roll;

            bool frozen =
                yawDelta < YawFreezeEpsDeg &&
                pitchDelta < PitchFreezeEpsDeg &&
                rollDelta < RollFreezeEpsDeg;

            if (frozen)
                frozenFrameCount++;
            else
                frozenFrameCount = 0;

            return frozenFrameCount >= PauseDetectFrames;
        }

        private void HandlePausedFrame()
        {
            if (!wasPaused)
            {
                wasPaused = true;
                resumeBlendFramesRemaining = 0;
            }

            // Hold pose channels exactly where they were.
            // Gently decay dynamic channels toward zero.
            for (int i = 0; i < DynamicChannelIndices.Length; i++)
            {
                int idx = DynamicChannelIndices[i];
                disp[idx] *= PauseDynamicDecay;
            }

            disp[IDX_YAW_DELTA] = 0f;
        }

        private float ReadSlotOrZero(int slot, double minAllowed, double maxAllowed)
        {
            double v = ReadValidatedSlot(slot, minAllowed, maxAllowed);
            return double.IsNaN(v) ? 0f : (float)v;
        }

        private double ReadValidatedSlot(int slot, double minAllowed, double maxAllowed)
        {
            lock (mmfLock)
            {
                if (accessor == null || slot < 0 || slot >= MaxSlots)
                    return double.NaN;

                try
                {
                    double v = accessor.ReadDouble(slot * sizeof(double));
                    return IsPlausible(v, minAllowed, maxAllowed) ? v : double.NaN;
                }
                catch
                {
                    return double.NaN;
                }
            }
        }

        private static bool IsPlausible(double v, double minAllowed, double maxAllowed)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
                return false;

            if (v < minAllowed || v > maxAllowed)
                return false;

            if (Math.Abs(v) > 1e12)
                return false;

            if (BitConverter.DoubleToInt64Bits(v) == unchecked((long)0x7272727272727272UL))
                return false;

            return true;
        }

        private void PushDisplayedInputs()
        {
            if (controller == null)
                return;

            for (int i = 0; i < CHANNEL_COUNT; i++)
                controller.SetInput(i, disp[i]);
        }

        private void MaybeReturnHome()
        {
            if (returnedHome)
                return;

            if (lastValidPacketTime == DateTime.MinValue)
                return;

            if ((DateTime.UtcNow - lastValidPacketTime).TotalMilliseconds <= StreamTimeoutMs)
                return;

            ReturnToHome();
        }

        private void ReturnToHome()
        {
            lock (homeLock)
            {
                if (returnedHome)
                    return;

                returnedHome = true;

                float[] start = new float[CHANNEL_COUNT];
                Array.Copy(disp, start, CHANNEL_COUNT);

                for (int step = 1; step <= HomeSteps; step++)
                {
                    float t = SmoothStep(step / (float)HomeSteps);

                    for (int i = 0; i < CHANNEL_COUNT; i++)
                    {
                        float value = Lerp(start[i], 0f, t);
                        controller?.SetInput(i, value);
                        disp[i] = value;
                    }

                    Thread.Sleep(HomeStepMs);
                }

                for (int i = 0; i < CHANNEL_COUNT; i++)
                {
                    controller?.SetInput(i, 0f);
                    disp[i] = 0f;
                }

                hadHeading = false;
                hadOrientationSample = false;
                yawAccumDeg = 0f;
                frozenFrameCount = 0;
                wasPaused = false;
                resumeBlendFramesRemaining = 0;
            }
        }

        private static float Wrap360(float deg)
        {
            deg %= 360f;
            if (deg < 0f) deg += 360f;
            return deg;
        }

        private static float NormalizeAngleDeltaDeg(float delta)
        {
            while (delta > 180f) delta -= 360f;
            while (delta < -180f) delta += 360f;
            return delta;
        }

        private static float SoftLimit(float value, float hardLimit, float softZone)
        {
            float softStart = hardLimit * softZone;
            float absVal = Math.Abs(value);

            if (absVal <= softStart)
                return value;

            float sign = Math.Sign(value);
            float excess = absVal - softStart;
            float remaining = hardLimit - softStart;

            if (remaining <= 0.0001f)
                return sign * hardLimit;

            float compressed = softStart + remaining * (float)Math.Tanh(excess / remaining);
            return sign * compressed;
        }

        private static float SoftLimitAsymmetric(float value, float posLimit, float negLimit, float softZone)
        {
            float hardLimit = value >= 0f ? posLimit : negLimit;
            return SoftLimit(value, hardLimit, softZone);
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * Clamp01(t);
        }

        private static float Clamp01(float t)
        {
            if (t < 0f) return 0f;
            if (t > 1f) return 1f;
            return t;
        }

        private static float SmoothStep(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        public void PatchGame()
        {
        }

        public Dictionary<string, ParameterInfo[]> GetFeatures()
        {
            return null;
        }

        public Type GetConfigBody()
        {
            return null;
        }

        private Stream GetStream(string resourceName)
        {
            Assembly asm = GetType().Assembly;
            return asm.GetManifestResourceStream(asm.GetName().Name + ".Resources." + resourceName);
        }
    }
}