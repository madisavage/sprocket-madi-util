using System;
using Hydropneumatic;
using Xunit;

namespace Tests
{
    /// <summary>The [adjust] tag is saved in vehicle files: editing it must keep the rest of the name.</summary>
    public class SuspensionTag
    {
        [Fact]
        public void RoundTrip()
        {
            string tagged = Plugin.WithSettings("Suspension [stab]", (120f, 40f));
            Assert.Equal((120f, 40f), Plugin.Settings(tagged));
            Assert.Equal("Suspension [stab]", Plugin.WithSettings(tagged, null));
        }

        [Fact]
        public void BareTagGivesDefaults() => Assert.Equal((150f, 50f), Plugin.Settings("Hull [adjust]"));

        [Fact]
        public void TravelOnlyGivesDefaultRate() => Assert.Equal((200f, 50f), Plugin.Settings("[ADJUST 200]"));

        [Fact]
        public void UntaggedGivesNull() => Assert.Null(Plugin.Settings("Suspension"));

        [Fact]
        public void BadNumbersGiveNull() => Assert.Null(Plugin.Settings("[adjust abc]"));

        [Fact]
        public void NegativeGivesNull() => Assert.Null(Plugin.Settings("[adjust -5]"));

        [Fact]
        public void MidNameTagLeavesNoDoubleSpace() => Assert.Equal("A B", Plugin.WithSettings("A [adjust 100 20] B", null));
    }

    /// <summary>Pose math: which arms move which way, and the travel limit.</summary>
    public class SuspensionPose
    {
        [Fact]
        public void PitchRaisesFrontLowersRear()
        {
            var s = new Pose.State(0f, 2f, 0f);
            Assert.True(Pose.Offset(s, 2f, 0f) > 0f);
            Assert.True(Pose.Offset(s, -2f, 0f) < 0f);
        }

        [Fact]
        public void RollRaisesRightSide()
        {
            var s = new Pose.State(0f, 0f, 3f);
            Assert.True(Pose.Offset(s, 0f, 1.2f) > 0f);
            Assert.True(Pose.Offset(s, 0f, -1.2f) < 0f);
        }

        [Fact]
        public void FullPitchMatchesTravel()
        {
            var s = Pose.Move(default, 0f, 90f, 0f, 150f, 2f, 1.2f);
            Assert.Equal(MathF.Atan(0.15f / 2f) * 180f / MathF.PI, s.Pitch, 3);
            Assert.Equal(150f, Pose.Worst(s, 2f, 1.2f), 2);
        }

        [Fact]
        public void RaiseAtFullPitchIsClamped()
        {
            var s = Pose.Move(default, 0f, 90f, 0f, 150f, 2f, 1.2f);
            s = Pose.Move(s, 50f, 0f, 0f, 150f, 2f, 1.2f);
            Assert.Equal(0f, s.Height, 3);
        }

        [Fact]
        public void TowardDoesNotOvershoot()
        {
            var s = Pose.Toward(new Pose.State(10f, 0f, 0f), default, 1f, 50f, 2f, 1.2f);
            Assert.Equal(0f, s.Height);
        }

        [Fact]
        public void TowardMovesAtRate()
        {
            var s = Pose.Toward(new Pose.State(100f, 0f, 0f), default, 0.5f, 50f, 2f, 1.2f);
            Assert.Equal(75f, s.Height, 3);
        }

        [Fact]
        public void PreloadRaisesRightSideForRightRoll()
        {
            var s = new Pose.State(10f, 3f, 2f);
            Assert.Equal((10f + 1000f * 1.2f * MathF.Tan(2f * MathF.PI / 180f)) / 1000f, Pose.Preload(s, 1f, 1.2f), 5);
            Assert.Equal((10f - 1000f * 1.2f * MathF.Tan(2f * MathF.PI / 180f)) / 1000f, Pose.Preload(s, -1f, 1.2f), 5);
        }

        [Fact]
        public void StrutsHaveNoNetForce()
        {
            var (front, rear, capped) = Pose.Struts(1000f, 2f, 2f, 4, 2, 1e6f, 12);
            Assert.Equal(0f, 4 * front + 2 * rear, 3);
            Assert.Equal(1000f, 4 * front * 2f - 2 * rear * 2f, 1);
            Assert.False(capped);
        }

        [Fact]
        public void StrutsCapAtLightEndLoad()
        {
            var (_, rear, capped) = Pose.Struts(1e7f, 2f, 2f, 4, 2, 120000f, 12);
            Assert.True(capped);
            Assert.Equal(-120000f * 2 / 12 / 2, rear, 1);
        }

        [Fact]
        public void NoseDownCapsAtFront()
        {
            var (front, _, capped) = Pose.Struts(-1e7f, 2f, 2f, 3, 6, 120000f, 12);
            Assert.True(capped);
            Assert.Equal(-120000f * 3 / 12 / 3, front, 1);
        }

        [Fact]
        public void StrutsNeedArmsAtBothEnds() => Assert.Equal((0f, 0f, false), Pose.Struts(1000f, 2f, 2f, 4, 0, 1e6f, 12));

        [Fact]
        public void ExtendedFrontWheelsMeanNoseUp()
        {
            // Front (+x) wheels hang 5 cm lower than the rear over 4 m: nose up atan(0.05 / 4)
            var wheels = new[] { (-2f, 0.06f), (-1f, 0.0475f), (1f, 0.0225f), (2f, 0.01f) };
            Assert.Equal(MathF.Atan(0.05f / 4f) * 180f / MathF.PI, Pose.TrackPitch(wheels), 3);
        }

        [Fact]
        public void EvenCompressionIsLevel() => Assert.Equal(0f, Pose.TrackPitch(new[] { (-2f, 0.03f), (0f, 0.03f), (2f, 0.03f) }), 5);

        [Fact]
        public void TrackPitchNeedsSpread() => Assert.Equal(0f, Pose.TrackPitch(new[] { (1f, 0.03f), (1f, 0.01f) }));
    }

    /// <summary>The [hullaim] tag is saved in vehicle files: editing it must keep the rest of the name.</summary>
    public class HullAimTag
    {
        [Fact]
        public void TagRoundTrip()
        {
            string on = HullAim.WithSettings("Gun [x]", (4.5f, 40f));
            Assert.Equal((4.5f, 40f), HullAim.Settings(on));
            Assert.Equal("Gun [x]", HullAim.WithSettings(on, null));
            Assert.Equal((HullAim.DefaultRate, HullAim.DefaultForce), HullAim.Settings("[HULLAIM] Gun"));
            Assert.Equal((2f, HullAim.DefaultForce), HullAim.Settings("Gun [hullaim 2]"));
            Assert.Null(HullAim.Settings("Gun [hullaim x]"));
            Assert.Null(HullAim.Settings("Gun"));
            Assert.Equal("A B", HullAim.WithSettings("A [hullaim] B", null));
        }
    }

    /// <summary>The hull-turn controller: dead zone, sign, saturation, damping, slew limit.</summary>
    public class HullAimSteer
    {
        [Fact]
        public void InsideDeadZoneIsZero() => Assert.Equal(0f, HullAim.Steer(0.1f, 0f, 0f, 1f, false).Steer);

        [Fact]
        public void SignFollowsError()
        {
            Assert.True(HullAim.Steer(5f, 0f, 0f, 1f, false).Steer > 0f);
            Assert.True(HullAim.Steer(-5f, 0f, 0f, 1f, false).Steer < 0f);
        }

        [Fact]
        public void Saturates() => Assert.Equal(1f, HullAim.Steer(90f, 0f, 0f, 1f, false).Steer);

        [Fact]
        public void TurningTowardTargetDamps() => Assert.True(HullAim.Steer(5f, 8f, 0f, 1f, false).Steer < HullAim.Steer(5f, 0f, 0f, 1f, false).Steer);

        [Fact]
        public void SlewLimited() => Assert.Equal(0.04f, HullAim.Steer(90f, 0f, 0f, 0.01f, false).Steer, 4);

        [Fact]
        public void BehindSaturatesOneWay() =>
            Assert.Equal(1f, MathF.Abs(HullAim.Steer(HullAim.Yaw(new(0, 0, 1), new(0, 0, -1), new(0, 1, 0)), 0f, 0f, 1f, false).Steer));

        [Fact]
        public void YawRightIsPositive() => Assert.True(HullAim.Yaw(new(0, 0, 1), new(1, 0, 1), new(0, 1, 0)) > 0f);

        [Fact]
        public void PitchUpIsPositive() => Assert.True(HullAim.Pitch(new(0, 0, 1), new(0, 1, 1), new(1, 0, 0)) > 0f);

        [Fact]
        public void WaitsPastStartThreshold() => Assert.Equal((0f, false), HullAim.Steer(1.5f, 0f, 0f, 1f, false));

        [Fact]
        public void LayingDriveTurnsTowardAimAndDamps()
        {
            Assert.True(HullAim.Lay(0.5f, 0f, 0f, 0.02f, 3f, false).Accel > 0f);
            Assert.True(HullAim.Lay(-0.5f, 0f, 0f, 0.02f, 3f, false).Accel < 0f);
            Assert.True(HullAim.Lay(0.5f, 5f, 0f, 0.02f, 3f, false).Accel < 0f);
        }

        [Fact]
        public void LayingDriveBuildsUntilItMoves()
        {
            // Stuck on friction: the push keeps growing
            var (first, integral) = HullAim.Lay(0.2f, 0f, 0f, 0.02f, 3f, false);
            for (int i = 0; i < 50; i++)
                integral = HullAim.Lay(0.2f, 0f, integral, 0.02f, 3f, false).Integral;
            Assert.True(HullAim.Lay(0.2f, 0f, integral, 0.02f, 3f, false).Accel > 5f * first);
        }

        [Fact]
        public void LayingDriveHoldsIntegralWhileCapped() => Assert.Equal(1f, HullAim.Lay(0.2f, 0f, 1f, 0.02f, 3f, true).Integral);

        [Fact]
        public void KeepsTurningUntilClose()
        {
            var (steer, turning) = HullAim.Steer(1.5f, 0f, 0.3f, 1f, true);
            Assert.True(turning);
            Assert.True(steer >= 0.25f);
            Assert.Equal((0f, false), HullAim.Steer(0.5f, 0f, 0f, 1f, true));
        }

        [Fact]
        public void NeverBelowMinimumWhileTurning() => Assert.True(HullAim.Steer(3f, 0f, 0f, 1f, false).Steer >= 0.25f);
    }
}
