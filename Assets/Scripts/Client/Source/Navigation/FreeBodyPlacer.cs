using Hazel;

namespace G1RoomNav
{
	/// <summary>
	/// Teleports a free-jointed MuJoCo body by writing its free joint's qpos (position + quaternion), the way
	/// RobotCafe puts its cup back. Poses are given in Hazel world space (Y up); the conversion to MuJoCo's
	/// axes and the scene translation is done here. Call right after a simulation reset, while the body is at
	/// rest: velocities are not touched.
	///
	/// A collider proxy body keeps the entity's own axes, so the body's origin is the entity's pivot: the
	/// centre for a scaled cube, the base for a mesh authored with its origin at the bottom.
	/// </summary>
	public static class FreeBodyPlacer
	{
		private const string ProxyBodyPrefix = "lucky_body_";

		/// <summary>The first qpos index of an entity's free joint, or uint.MaxValue if it has none.</summary>
		public static uint FindQposAdr(MujocoSceneComponent mujoco, Entity entity)
		{
			uint body = mujoco.GetBodyID(ProxyBodyPrefix + entity.ID);
			if (body == uint.MaxValue)
				return uint.MaxValue;
			uint joint = mujoco.GetBodyFreeJointIndex(body);
			return joint == uint.MaxValue ? uint.MaxValue : mujoco.GetJointQposAdr(joint);
		}

		/// <summary>Put the body at <paramref name="position"/>, turned <paramref name="yaw"/> radians about +Y.</summary>
		public static void SetPose(MujocoSceneComponent mujoco, uint qposAdr, Vector3 position, float yaw)
		{
			Quaternion hzToMj = mujoco.GetAxisHzToMj();
			Vector3 pMj = hzToMj * (position - mujoco.GetSceneTranslation());
			Quaternion qMj = (hzToMj * Planar.Yaw(yaw)).Normalized();
			mujoco.SetQposByIndex(qposAdr + 0, pMj.X);
			mujoco.SetQposByIndex(qposAdr + 1, pMj.Y);
			mujoco.SetQposByIndex(qposAdr + 2, pMj.Z);
			mujoco.SetQposByIndex(qposAdr + 3, qMj.W);
			mujoco.SetQposByIndex(qposAdr + 4, qMj.X);
			mujoco.SetQposByIndex(qposAdr + 5, qMj.Y);
			mujoco.SetQposByIndex(qposAdr + 6, qMj.Z);
		}
	}
}
