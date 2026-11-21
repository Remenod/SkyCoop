using System.Diagnostics;
using UnityEngine;

namespace SkyCoop
{
    /// <summary>
    /// Monotonic clock shared by the movement sender and the interpolation on the receiver.
    /// Unaffected by Time.timeScale, pause menus and frame hitches, and double precision,
    /// so timestamps stay exact over long sessions.
    /// </summary>
    public static class NetClock
    {
        public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    }

    /// <summary>
    /// Snapshot interpolation for a remote player.
    ///
    /// The owner sends its transform at a fixed rate (PlayersManager.c_TransformSendRate),
    /// stamped with its own NetClock. We render the player InterpolationDelay seconds behind
    /// the newest snapshot, so there is nearly always a snapshot on each side of the render time
    /// and movement is a straight interpolation between two real samples.
    ///
    /// The render clock advances in local real time and drifts gently toward
    /// "newest - InterpolationDelay", which absorbs network jitter without visible speed changes.
    /// It never extrapolates: when the stream starves, the last pose is held.
    /// </summary>
    public class RemoteMotion
    {
        private struct Snapshot
        {
            public double Time;
            public Vector3 Position;
            public Quaternion Rotation;
            public float Tilt;
        }

        // About three send intervals at 30 Hz: one or two lost packets are bridged by interpolation.
        public const double InterpolationDelay = 0.1;

        // A gap longer than this means the sender paused (menu, loading, suspended gameplay)
        // or the link dropped for a while. Interpolating across it would slide the model, so we jump.
        private const double MaxSnapshotGap = 1.0;

        // If the render clock falls this far behind its target, jump instead of catching up slowly.
        private const double MaxRenderLag = 0.5;

        // Maximum rate at which the render clock speeds up or slows down to track its target.
        private const double MaxClockCorrection = 0.1;

        // A jump of at least TeleportMinDistance metres at an implied speed above TeleportSpeed m/s is
        // a teleport (respawn, scene spawn point, getting out of a bed), not movement.
        private const float TeleportMinDistance = 3f;
        private const float TeleportSpeed = 25f;

        private const int MaxSnapshots = 64;

        // Smoothing rate for the velocity handed to the animator, per second.
        private const float VelocitySmoothing = 15f;

        private readonly List<Snapshot> m_Snapshots = new List<Snapshot>();
        private double m_RenderTime = 0;
        private bool m_HasRenderTime = false;
        private double m_LastLocalTime = 0;

        public Vector3 Position { get; private set; } = Vector3.zero;
        public Quaternion Rotation { get; private set; } = Quaternion.identity;
        public float Tilt { get; private set; } = 0;

        // Velocity of the interpolated movement, smoothed for the animator.
        public Vector3 Velocity { get; private set; } = Vector3.zero;

        public bool HasData => m_Snapshots.Count > 0;

        public void Clear()
        {
            m_Snapshots.Clear();
            m_HasRenderTime = false;
            Velocity = Vector3.zero;
        }

        public void Push(double SenderTime, Vector3 Pos, Quaternion Rot, float TiltAngle)
        {
            if (m_Snapshots.Count > 0)
            {
                Snapshot Newest = m_Snapshots[m_Snapshots.Count - 1];
                double Gap = SenderTime - Newest.Time;

                if (Gap <= 0)
                {
                    // Packets are sequenced, so an older timestamp means the sender's clock restarted.
                    Clear();
                }
                else if (Gap > MaxSnapshotGap)
                {
                    Clear();
                }
                else
                {
                    float Distance = Vector3.Distance(Pos, Newest.Position);
                    if (Distance > TeleportMinDistance && Distance > TeleportSpeed * (float)Gap)
                    {
                        Clear();
                    }
                }
            }

            m_Snapshots.Add(new Snapshot { Time = SenderTime, Position = Pos, Rotation = Rot, Tilt = TiltAngle });

            if (m_Snapshots.Count > MaxSnapshots)
            {
                m_Snapshots.RemoveAt(0);
            }
        }

        /// <summary>
        /// Advances the render clock to LocalTime (NetClock.Now) and samples the pose.
        /// Returns false when there is nothing to show yet.
        /// </summary>
        public bool Sample(double LocalTime)
        {
            double Elapsed = m_LastLocalTime > 0 ? LocalTime - m_LastLocalTime : 0;
            m_LastLocalTime = LocalTime;

            if (m_Snapshots.Count == 0)
            {
                return false;
            }

            double Newest = m_Snapshots[m_Snapshots.Count - 1].Time;
            double Target = Newest - InterpolationDelay;

            if (!m_HasRenderTime || Target - m_RenderTime > MaxRenderLag)
            {
                m_RenderTime = Target;
                m_HasRenderTime = true;
            }
            else
            {
                double Error = Target - m_RenderTime; // positive: we are behind, run slightly faster
                double Scale = 1.0 + Math.Clamp(Error, -MaxClockCorrection, MaxClockCorrection);
                m_RenderTime += Elapsed * Scale;

                if (m_RenderTime > Newest)
                {
                    m_RenderTime = Newest; // starved: hold the newest pose, never extrapolate
                }
            }

            // Keep exactly one snapshot at or before the render time.
            while (m_Snapshots.Count >= 2 && m_Snapshots[1].Time <= m_RenderTime)
            {
                m_Snapshots.RemoveAt(0);
            }

            Snapshot A = m_Snapshots[0];
            Vector3 SegmentVelocity = Vector3.zero;

            if (m_Snapshots.Count == 1 || m_RenderTime <= A.Time)
            {
                Position = A.Position;
                Rotation = A.Rotation;
                Tilt = A.Tilt;
            }
            else
            {
                Snapshot B = m_Snapshots[1];
                double Duration = B.Time - A.Time;
                float T = Mathf.Clamp01((float)((m_RenderTime - A.Time) / Duration));

                Position = Vector3.Lerp(A.Position, B.Position, T);
                Rotation = Quaternion.Slerp(A.Rotation, B.Rotation, T);
                // Camera pitch comes from localEulerAngles and wraps at 0/360.
                Tilt = Mathf.LerpAngle(A.Tilt, B.Tilt, T);
                SegmentVelocity = (B.Position - A.Position) / (float)Duration;
            }

            float Blend = 1f - Mathf.Exp(-VelocitySmoothing * (float)Elapsed);
            Velocity = Vector3.Lerp(Velocity, SegmentVelocity, Blend);

            return true;
        }
    }
}
