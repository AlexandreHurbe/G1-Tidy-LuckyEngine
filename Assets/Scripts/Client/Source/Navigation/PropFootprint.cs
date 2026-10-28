using System;
using Hazel;

namespace G1RoomNav
{
	/// <summary>
	/// Where a prop stands and how much floor it takes: an oriented rectangle around all of its collider shapes,
	/// plus the height of its highest surface. "Axes" are the footprint's own: local X = (cos yaw, -sin yaw) and
	/// local Z = (sin yaw, cos yaw) in (x, z), the same convention as <see cref="LayoutRandomizer"/>.
	/// </summary>
	public readonly struct Footprint
	{
		/// <summary>Floor position of the rectangle's centre (world space).</summary>
		public readonly float CenterX, CenterZ;
		/// <summary>Half sizes along the footprint's own axes.</summary>
		public readonly float HalfX, HalfZ;
		/// <summary>Heading of the footprint's X axis about +Y (radians).</summary>
		public readonly float Yaw;
		/// <summary>Height of the highest collider surface above the world floor.</summary>
		public readonly float Top;
		/// <summary>The entity's pivot (where its transform is), world space.</summary>
		public readonly float PivotX, PivotY, PivotZ;
		/// <summary>The rectangle's centre relative to the pivot, along the footprint's axes.</summary>
		public readonly float OffU, OffV;
		/// <summary>False if it had to be estimated from the entity's scale (no collider geometry was found).</summary>
		public readonly bool Measured;

		public Footprint(float cx, float cz, float hx, float hz, float yaw, float top, float px, float py, float pz, float offU, float offV, bool measured)
		{
			CenterX = cx; CenterZ = cz; HalfX = hx; HalfZ = hz; Yaw = yaw; Top = top;
			PivotX = px; PivotY = py; PivotZ = pz; OffU = offU; OffV = offV; Measured = measured;
		}
	}

	/// <summary>
	/// Measures a prop's <see cref="Footprint"/> from the MuJoCo geometry of its collider body. Props from the cafe
	/// are meshes at scale 1 with compound colliders, so the entity's scale says nothing about their size; the
	/// collider shapes do. Primitive boxes scaled to size measure the same as their scale, so one code path serves both.
	/// </summary>
	public static class PropFootprint
	{
		private const string ProxyBodyPrefix = "lucky_body_";
		// MuJoCo mjtGeom
		private const int GeomPlane = 0, GeomHField = 1, GeomSphere = 2, GeomCapsule = 3, GeomCylinder = 5;

		public static Footprint Measure(MujocoSceneComponent mujoco, WorldFrame frame, Entity entity)
		{
			uint body = mujoco.GetBodyID(ProxyBodyPrefix + entity.ID);
			if (body == uint.MaxValue || !mujoco.GetBodyPoseMj(body, out Vector3 bodyMj, out Quaternion bodyRotMj, out _, out _))
				return Estimate(entity);

			Vector3 pivot = frame.Point(bodyMj);
			float yaw = Planar.Heading(frame.Rotation(bodyRotMj));
			float cos = MathF.Cos(yaw), sin = MathF.Sin(yaw);

			float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue, top = float.MinValue;
			int shapes = 0;

			void Add(Vector3 p)
			{
				Vector3 d = p - pivot;
				float u = d.X * cos - d.Z * sin;
				float v = d.X * sin + d.Z * cos;
				minU = MathF.Min(minU, u); maxU = MathF.Max(maxU, u);
				minV = MathF.Min(minV, v); maxV = MathF.Max(maxV, v);
				top = MathF.Max(top, p.Y);
			}

			uint count = mujoco.ModelNgeom;
			for (uint g = 0; g < count; g++)
			{
				if (!mujoco.GetGeomDescriptor(g, out int geomBody, out int type, out _, out int contype, out int conaffinity, out Vector3 size))
					continue;
				if (geomBody != (int)body || (contype == 0 && conaffinity == 0) || type == GeomPlane || type == GeomHField)
					continue;
				if (!mujoco.GetGeomPoseMj(g, out Vector3 pMj, out Quaternion qMj))
					continue;

				Vector3 c = frame.Point(pMj);
				Quaternion r = frame.Rotation(qMj);
				Vector3 ax = r * new Vector3(1f, 0f, 0f), ay = r * new Vector3(0f, 1f, 0f), az = r * new Vector3(0f, 0f, 1f);
				shapes++;

				if (type == GeomSphere)
				{
					float rad = size.X;
					Add(c + new Vector3(rad, 0f, 0f)); Add(c - new Vector3(rad, 0f, 0f));
					Add(c + new Vector3(0f, 0f, rad)); Add(c - new Vector3(0f, 0f, rad));
					Add(c + new Vector3(0f, rad, 0f)); Add(c - new Vector3(0f, rad, 0f));
				}
				else if (type == GeomCylinder || type == GeomCapsule)
				{
					// Radius size.X, half length size.Y along the geom's local Z; a capsule also has rounded ends.
					float half = type == GeomCapsule ? size.Y + size.X : size.Y;
					for (int end = -1; end <= 1; end += 2)
					{
						Vector3 e = c + az * (half * end);
						for (int i = 0; i < 16; i++)
						{
							float a = MathF.PI * 2f * i / 16f;
							Add(e + ax * (size.X * MathF.Cos(a)) + ay * (size.X * MathF.Sin(a)));
						}
					}
				}
				else
				{
					// Box, ellipsoid, mesh: the bounding box (size holds the half extents).
					for (int sx = -1; sx <= 1; sx += 2)
						for (int sy = -1; sy <= 1; sy += 2)
							for (int sz = -1; sz <= 1; sz += 2)
								Add(c + ax * (size.X * sx) + ay * (size.Y * sy) + az * (size.Z * sz));
				}
			}

			if (shapes == 0)
				return Estimate(entity);

			float offU = 0.5f * (minU + maxU), offV = 0.5f * (minV + maxV);
			float cx = pivot.X + cos * offU + sin * offV;
			float cz = pivot.Z - sin * offU + cos * offV;
			return new Footprint(cx, cz, 0.5f * (maxU - minU), 0.5f * (maxV - minV), yaw, top, pivot.X, pivot.Y, pivot.Z, offU, offV, true);
		}

		/// <summary>No collider geometry found: assume a cube scaled to the prop's size (what the box furniture is).</summary>
		private static Footprint Estimate(Entity e)
		{
			Vector3 t = e.Translation, s = e.Scale;
			return new Footprint(t.X, t.Z, 0.5f * s.X, 0.5f * s.Z, e.Rotation.Y, t.Y + 0.5f * s.Y, t.X, t.Y, t.Z, 0f, 0f, false);
		}
	}
}
