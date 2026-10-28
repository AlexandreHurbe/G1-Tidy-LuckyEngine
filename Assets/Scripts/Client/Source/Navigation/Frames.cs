// Copied from the RobotCafe example project (Cafe.Robotics), namespace changed.
using Hazel;

namespace G1RoomNav
{
	/// <summary>
	/// Converts MuJoCo world-space data (Z-up, origin at the physics root entity) to Hazel world
	/// space (Y-up). Everything above the robot layer works in Hazel world space.
	/// </summary>
	public readonly struct WorldFrame
	{
		private readonly Quaternion m_MjToHz;
		private readonly Vector3 m_Origin;

		public WorldFrame(MujocoSceneComponent mujoco)
		{
			m_MjToHz = mujoco.GetAxisHzToMj().Conjugate;
			m_Origin = mujoco.GetSceneTranslation();
		}

		public Vector3 Point(Vector3 mujocoPoint) => m_MjToHz * mujocoPoint + m_Origin;
		public Vector3 Vector(Vector3 mujocoVector) => m_MjToHz * mujocoVector;
		public Quaternion Rotation(Quaternion mujocoRotation) => (m_MjToHz * mujocoRotation).Normalized();
	}

	/// <summary>
	/// Planar helpers for the floor (Hazel XZ plane). A heading is the yaw about +Y in radians:
	/// zero along +X, positive counter-clockwise seen from above. It equals MuJoCo's yaw about +Z.
	/// </summary>
	public static class Planar
	{
		public static Vector3 Forward(float heading) => new Vector3(Mathf.Cos(heading), 0f, -Mathf.Sin(heading));

		/// <summary>Unit vector to the left of <paramref name="heading"/> (up x forward).</summary>
		public static Vector3 Left(float heading) => new Vector3(-Mathf.Sin(heading), 0f, -Mathf.Cos(heading));

		public static float Heading(Vector3 direction) => Mathf.Atan2(-direction.Z, direction.X);

		public static float Heading(Quaternion rotation) => Heading(rotation * new Vector3(1f, 0f, 0f));

		public static float HeadingTo(Vector3 from, Vector3 to) => Mathf.Atan2(-(to.Z - from.Z), to.X - from.X);

		public static float Distance(Vector3 a, Vector3 b)
		{
			float dx = b.X - a.X, dz = b.Z - a.Z;
			return Mathf.Sqrt(dx * dx + dz * dz);
		}

		public static Vector3 Flatten(Vector3 v) => new Vector3(v.X, 0f, v.Z);

		public static float AngleDifference(float target, float current) => Mathf.WrapToPi(target - current);

		/// <summary>Rotation about world up by <paramref name="heading"/> (maps +X onto Forward(heading)).</summary>
		public static Quaternion Yaw(float heading) => new Quaternion(0f, Mathf.Sin(0.5f * heading), 0f, Mathf.Cos(0.5f * heading));

		/// <summary>Rotation of a frame whose axes are given as orthonormal world vectors.</summary>
		public static Quaternion FromAxes(Vector3 x, Vector3 y, Vector3 z)
		{
			float m00 = x.X, m10 = x.Y, m20 = x.Z;
			float m01 = y.X, m11 = y.Y, m21 = y.Z;
			float m02 = z.X, m12 = z.Y, m22 = z.Z;
			float trace = m00 + m11 + m22;
			Quaternion q;
			if (trace > 0f)
			{
				float s = Mathf.Sqrt(trace + 1f) * 2f;
				q = new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
			}
			else if (m00 > m11 && m00 > m22)
			{
				float s = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f;
				q = new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
			}
			else if (m11 > m22)
			{
				float s = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f;
				q = new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
			}
			else
			{
				float s = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f;
				q = new Quaternion((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
			}
			return q.Normalized();
		}

		/// <summary>Angle between two rotations, in radians.</summary>
		public static float AngleBetween(Quaternion a, Quaternion b)
		{
			float d = Mathf.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
			return 2f * Mathf.Acos(Mathf.Min(1f, d));
		}
	}

	/// <summary>A pose on the floor: a position and the heading the robot should face.</summary>
	public readonly struct FloorPose
	{
		public readonly Vector3 Position;
		public readonly float Heading;

		public FloorPose(Vector3 position, float heading)
		{
			Position = Planar.Flatten(position);
			Heading = heading;
		}

		public override string ToString() => $"({Position.X:F2}, {Position.Z:F2}, heading {Heading * Mathf.Rad2Deg:F0} deg)";
	}

	/// <summary>A horizontal frame on the floor: origin and heading; (forward, left, up) coordinates.</summary>
	public readonly struct BodyFrame
	{
		public readonly Vector3 Origin;
		public readonly float Heading;
		public readonly Vector3 Forward;
		public readonly Vector3 Left;

		public BodyFrame(Vector3 origin, float heading)
		{
			Origin = origin;
			Heading = heading;
			Forward = Planar.Forward(heading);
			Left = Planar.Left(heading);
		}

		/// <summary>World point at (forward, left, up) metres from the frame origin.</summary>
		public Vector3 ToWorld(float forward, float left, float up) =>
			Origin + Forward * forward + Left * left + new Vector3(0f, up, 0f);

		public Vector3 ToWorld(Vector3 local) => ToWorld(local.X, local.Y, local.Z);

		/// <summary>(forward, left, up) coordinates of a world point.</summary>
		public Vector3 ToLocal(Vector3 world)
		{
			Vector3 d = world - Origin;
			return new Vector3(Vector3.Dot(d, Forward), Vector3.Dot(d, Left), d.Y);
		}
	}
}
