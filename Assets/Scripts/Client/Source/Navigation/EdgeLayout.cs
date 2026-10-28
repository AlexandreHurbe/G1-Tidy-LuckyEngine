using System;
using System.Collections.Generic;
using Hazel;

namespace G1RoomNav
{
	/// <summary>
	/// Points along one edge of a static box's top face: where items are scattered on the counter, and where
	/// the delivery spots are on the dining table. The robot can only reach about 11 cm in from an edge, so
	/// everything is placed a few centimetres in from it.
	///
	/// The box is described by a <see cref="Footprint"/> (measured from the collider shapes by
	/// <see cref="PropFootprint"/>), whose axes follow its yaw (local X = (cos, -sin), local Z = (sin, cos)),
	/// the same convention as <see cref="LayoutRandomizer"/>.
	/// </summary>
	public static class EdgeLayout
	{
		/// <summary>One edge of the top face.</summary>
		public readonly struct Edge
		{
			/// <summary>Middle of the edge on the top face (Y is the top height).</summary>
			public readonly Vector3 Mid;
			/// <summary>Horizontal unit vector pointing out of the box, away from its centre.</summary>
			public readonly Vector3 Normal;
			/// <summary>Horizontal unit vector along the edge.</summary>
			public readonly Vector3 Tangent;
			public readonly float Length;
			public float Top => Mid.Y;

			public Edge(Vector3 mid, Vector3 normal, Vector3 tangent, float length)
			{
				Mid = mid;
				Normal = normal;
				Tangent = tangent;
				Length = length;
			}

			/// <summary>
			/// A point on the top face, <paramref name="along"/> metres from the middle of the edge and
			/// <paramref name="inset"/> metres in from it.
			/// </summary>
			public Vector3 Point(float along, float inset) => Mid + Tangent * along - Normal * inset;

			/// <summary>A point on the floor-side of the edge, <paramref name="out"/> metres away from it.</summary>
			public Vector3 Outside(float along, float outDistance) => Mid + Tangent * along + Normal * outDistance;
		}

		/// <summary>
		/// The edge of <paramref name="box"/> that faces <paramref name="roomCenter"/> most, among edges at least
		/// <paramref name="minLength"/> long (so a long side is chosen over a short one).
		/// </summary>
		public static Edge FacingRoom(Footprint box, Vector3 roomCenter, float minLength)
		{
			// The footprint's centre and size, not the entity's transform: cafe props are scale-1 meshes whose
			// transform sits at the floor under one end. Width runs along the footprint's X, depth along Z.
			Vector3 c = new Vector3(box.CenterX, 0f, box.CenterZ);
			Vector3 s = new Vector3(2f * box.HalfX, 0f, 2f * box.HalfZ);
			float yaw = box.Yaw;
			float cos = MathF.Cos(yaw), sin = MathF.Sin(yaw);
			Vector3 axisX = new Vector3(cos, 0f, -sin);
			Vector3 axisZ = new Vector3(sin, 0f, cos);
			float top = box.Top;
			Vector3 toRoom = new Vector3(roomCenter.X - c.X, 0f, roomCenter.Z - c.Z);

			// Candidates: out along +-X (edge runs along Z, length s.Z) and +-Z (edge runs along X, length s.X).
			var candidates = new List<Edge>
			{
				new Edge(new Vector3(c.X, top, c.Z) + axisX * (0.5f * s.X),  axisX,  axisZ, s.Z),
				new Edge(new Vector3(c.X, top, c.Z) - axisX * (0.5f * s.X), -axisX, -axisZ, s.Z),
				new Edge(new Vector3(c.X, top, c.Z) + axisZ * (0.5f * s.Z),  axisZ, -axisX, s.X),
				new Edge(new Vector3(c.X, top, c.Z) - axisZ * (0.5f * s.Z), -axisZ,  axisX, s.X),
			};

			Edge best = candidates[0];
			float bestScore = float.MinValue;
			foreach (Edge e in candidates)
			{
				if (e.Length < minLength)
					continue;
				float score = e.Normal.X * toRoom.X + e.Normal.Z * toRoom.Z;
				if (score > bestScore)
				{
					bestScore = score;
					best = e;
				}
			}
			return best;
		}

		/// <summary><paramref name="count"/> evenly spaced points, <paramref name="margin"/> in from each end of the edge.</summary>
		public static List<Vector3> Spots(Edge edge, int count, float inset, float margin)
		{
			var points = new List<Vector3>(count);
			float usable = Math.Max(0f, edge.Length - 2f * margin);
			for (int i = 0; i < count; i++)
			{
				float t = count == 1 ? 0f : (i / (float)(count - 1) - 0.5f);
				points.Add(edge.Point(t * usable, inset));
			}
			return points;
		}

		/// <summary>
		/// <paramref name="count"/> random points along the edge, at least <paramref name="minSpacing"/> apart,
		/// <paramref name="insetMin"/>..<paramref name="insetMax"/> in from it. Returns fewer if they do not fit.
		/// </summary>
		public static List<Vector3> RandomPoints(Edge edge, int count, float insetMin, float insetMax, float margin, float minSpacing, System.Random rng)
		{
			var along = new List<float>(count);
			float half = 0.5f * edge.Length - margin;
			for (int attempt = 0; attempt < 400 && along.Count < count; attempt++)
			{
				float u = (float)(rng.NextDouble() * 2.0 - 1.0) * half;
				bool clear = true;
				foreach (float other in along)
					if (MathF.Abs(other - u) < minSpacing)
						clear = false;
				if (clear)
					along.Add(u);
			}
			var points = new List<Vector3>(along.Count);
			foreach (float u in along)
				points.Add(edge.Point(u, insetMin + (float)rng.NextDouble() * (insetMax - insetMin)));
			return points;
		}
	}
}
